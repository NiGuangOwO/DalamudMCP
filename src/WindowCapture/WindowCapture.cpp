#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dwmapi.h>
#include <tlhelp32.h>
#include <wincodec.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <algorithm>
#include <cerrno>
#include <cmath>
#include <cstdint>
#include <string_view>
#include <vector>

using namespace winrt;
namespace Capture = winrt::Windows::Graphics::Capture;
using winrt::Windows::Graphics::SizeInt32;
using winrt::Windows::Graphics::DirectX::DirectXPixelFormat;
using winrt::Windows::Graphics::DirectX::Direct3D11::IDirect3DDevice;

namespace {
enum ExitCode { InvalidTarget = 2, WindowUnavailable, Unsupported, Deadline, GeometryChanged, CaptureFailed };

struct Geometry {
    DWORD thread;
    RECT frame, client;
    bool operator==(const Geometry& other) const {
        return thread == other.thread && EqualRect(&frame, &other.frame) && EqualRect(&client, &other.client);
    }
};

DWORD ParentPid() {
    handle snapshot{CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)};
    if (snapshot.get() == INVALID_HANDLE_VALUE) {
        snapshot.detach();
        return 0;
    }
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    for (auto more = Process32FirstW(snapshot.get(), &entry); more; more = Process32NextW(snapshot.get(), &entry))
        if (entry.th32ProcessID == GetCurrentProcessId()) return entry.th32ParentProcessID;
    return 0;
}

bool BoundedSize(int width, int height) {
    return width > 0 && height > 0 && width <= 16384 && height <= 16384 &&
        static_cast<int64_t>(width) * height <= 67108864;
}

Geometry Probe(HWND hwnd, DWORD owner, SizeInt32 size) {
    DWORD pid = 0;
    auto thread = GetWindowThreadProcessId(hwnd, &pid);
    if (!thread || pid != owner || GetAncestor(hwnd, GA_ROOT) != hwnd) throw InvalidTarget;
    DWORD cloaked = 0;
    if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsHungAppWindow(hwnd) ||
        (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked))
        throw WindowUnavailable;
    const auto matches = [size](const RECT& r) { return r.right - r.left == size.Width && r.bottom - r.top == size.Height; };
    Geometry g{thread, {}, {}};
    // WGC normally uses the DWM frame. Accept Win32 bounds only if they match the physical frame size.
    if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &g.frame, sizeof(g.frame))) || !matches(g.frame)) {
        if (!GetWindowRect(hwnd, &g.frame) || !matches(g.frame)) throw GeometryChanged;
    }
    POINT origin{};
    if (!GetClientRect(hwnd, &g.client) || !ClientToScreen(hwnd, &origin)) throw WindowUnavailable;
    OffsetRect(&g.client, origin.x - g.frame.left, origin.y - g.frame.top);
    return g;
}

void Write(const void* data, DWORD length) {
    auto ptr = static_cast<const uint8_t*>(data);
    const auto out = GetStdHandle(STD_OUTPUT_HANDLE);
    while (length) {
        DWORD written = 0;
        if (!WriteFile(out, ptr, std::min<DWORD>(length, 256 * 1024), &written, nullptr) || !written)
            throw CaptureFailed;
        ptr += written;
        length -= written;
    }
}

void Run(HWND hwnd, DWORD pid, bool clientOnly, int cap, ULONGLONG deadline) {
    if (!Capture::GraphicsCaptureSession::IsSupported()) throw Unsupported;
    auto interop = get_activation_factory<Capture::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    Capture::GraphicsCaptureItem item{nullptr};
    const auto hr = interop->CreateForWindow(hwnd, guid_of<Capture::IGraphicsCaptureItem>(), put_abi(item));
    if (FAILED(hr)) throw WindowUnavailable;
    const auto size = item.Size();
    if (!BoundedSize(size.Width, size.Height)) throw GeometryChanged;
    const auto geometry = Probe(hwnd, pid, size);
    const RECT crop = clientOnly ? geometry.client : RECT{0, 0, size.Width, size.Height};
    const int width = crop.right - crop.left, height = crop.bottom - crop.top;
    if (!BoundedSize(width, height) || crop.left < 0 || crop.top < 0 ||
        crop.right > size.Width || crop.bottom > size.Height) throw GeometryChanged;

    com_ptr<ID3D11Device> d3d;
    com_ptr<ID3D11DeviceContext> context;
    // This device belongs only to the worker; it is never the game's immediate context.
    check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, d3d.put(), nullptr, context.put()));
    auto dxgi = d3d.as<IDXGIDevice>();
    com_ptr<::IInspectable> inspectable;
    check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
    auto pool = Capture::Direct3D11CaptureFramePool::CreateFreeThreaded(inspectable.as<IDirect3DDevice>(),
        DirectXPixelFormat::B8G8R8A8UIntNormalized, 1, size);
    auto session = pool.CreateCaptureSession(item);
    if (auto cursor = session.try_as<Capture::IGraphicsCaptureSession2>()) cursor.IsCursorCaptureEnabled(false);
    session.StartCapture();

    Capture::Direct3D11CaptureFrame frame{nullptr};
    while (GetTickCount64() < deadline && !(frame = pool.TryGetNextFrame())) Sleep(8);
    if (!frame) throw Deadline;
    auto content = frame.ContentSize();
    if (content.Width != size.Width || content.Height != size.Height) throw GeometryChanged;
    using Access = ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;
    auto access = frame.Surface().as<Access>();
    com_ptr<ID3D11Texture2D> source;
    check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), source.put_void()));
    D3D11_TEXTURE2D_DESC description{};
    source->GetDesc(&description);
    if (description.Width < static_cast<UINT>(crop.right) || description.Height < static_cast<UINT>(crop.bottom) ||
        description.Format != DXGI_FORMAT_B8G8R8A8_UNORM || description.SampleDesc.Count != 1) throw GeometryChanged;
    D3D11_TEXTURE2D_DESC staging{static_cast<UINT>(width), static_cast<UINT>(height), 1, 1,
        DXGI_FORMAT_B8G8R8A8_UNORM, {1, 0}, D3D11_USAGE_STAGING, 0, D3D11_CPU_ACCESS_READ, 0};
    com_ptr<ID3D11Texture2D> copy;
    check_hresult(d3d->CreateTexture2D(&staging, nullptr, copy.put()));
    const D3D11_BOX box{static_cast<UINT>(crop.left), static_cast<UINT>(crop.top), 0,
        static_cast<UINT>(crop.right), static_cast<UINT>(crop.bottom), 1};
    context->CopySubresourceRegion(copy.get(), 0, 0, 0, 0, source.get(), 0, &box);
    context->Flush();
    D3D11_MAPPED_SUBRESOURCE mapped{};
    HRESULT map;
    do {
        if (GetTickCount64() >= deadline) throw Deadline;
        map = context->Map(copy.get(), 0, D3D11_MAP_READ, D3D11_MAP_FLAG_DO_NOT_WAIT, &mapped);
        if (map == DXGI_ERROR_WAS_STILL_DRAWING) Sleep(4);
    } while (map == DXGI_ERROR_WAS_STILL_DRAWING);
    check_hresult(map);
    struct Unmap { ID3D11DeviceContext* context; ID3D11Texture2D* copy; ~Unmap() { context->Unmap(copy, 0); } };
    const Unmap unmap{context.get(), copy.get()};
    if (!mapped.pData || mapped.RowPitch < static_cast<UINT>(width * 4)) throw CaptureFailed;

    const double scale = std::min(1.0, static_cast<double>(cap) / std::max(width, height));
    const int outW = std::max(1, static_cast<int>(std::nearbyint(width * scale)));
    const int outH = std::max(1, static_cast<int>(std::nearbyint(height * scale)));
    std::vector<uint8_t> scaled;
    if (outW != width || outH != height) {
        com_ptr<IWICImagingFactory> factory;
        check_hresult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
            __uuidof(IWICImagingFactory), factory.put_void()));
        com_ptr<IWICBitmap> bitmap;
        check_hresult(factory->CreateBitmapFromMemory(width, height, GUID_WICPixelFormat32bppBGRA, mapped.RowPitch,
            mapped.RowPitch * (height - 1) + width * 4, static_cast<BYTE*>(mapped.pData), bitmap.put()));
        com_ptr<IWICBitmapScaler> scaler;
        check_hresult(factory->CreateBitmapScaler(scaler.put()));
        check_hresult(scaler->Initialize(bitmap.get(), outW, outH, WICBitmapInterpolationModeFant));
        scaled.resize(static_cast<size_t>(outW) * outH * 4);
        check_hresult(scaler->CopyPixels(nullptr, outW * 4, static_cast<UINT>(scaled.size()), scaled.data()));
    }

    if (!(Probe(hwnd, pid, size) == geometry)) throw GeometryChanged;
    // DMC1 followed by little-endian dimensions and tightly packed BGRA rows. No desktop fallback.
    const uint32_t header[]{0x31434D44, static_cast<uint32_t>(outW), static_cast<uint32_t>(outH),
        static_cast<uint32_t>(width), static_cast<uint32_t>(height)};
    Write(header, sizeof(header));
    if (!scaled.empty()) Write(scaled.data(), static_cast<DWORD>(scaled.size()));
    else for (int y = 0; y < height; ++y)
        Write(static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch, width * 4);
}

uint64_t Number(const wchar_t* text) {
    if (!*text || !std::all_of(text, text + wcslen(text), [](wchar_t c) { return c >= L'0' && c <= L'9'; }))
        throw InvalidTarget;
    errno = 0;
    const auto number = _wcstoui64(text, nullptr, 10);
    if (errno == ERANGE) throw InvalidTarget;
    return number;
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        if (argc != 6 || GetFileType(GetStdHandle(STD_OUTPUT_HANDLE)) != FILE_TYPE_PIPE) return InvalidTarget;
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return Unsupported;
        const auto window = Number(argv[1]), pid = Number(argv[2]), cap = Number(argv[4]), budget = Number(argv[5]);
        const std::wstring_view area{argv[3]};
        if (!window || !pid || pid > MAXDWORD || static_cast<DWORD>(pid) != ParentPid() ||
            (area != L"client" && area != L"window") || cap < 256 || cap > 3840 || budget < 100 || budget > 10000)
            return InvalidTarget;
        DWORD owner = 0;
        auto hwnd = reinterpret_cast<HWND>(window);
        if (!GetWindowThreadProcessId(hwnd, &owner) || owner != pid || GetAncestor(hwnd, GA_ROOT) != hwnd)
            return InvalidTarget;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsHungAppWindow(hwnd)) return WindowUnavailable;
        init_apartment(apartment_type::multi_threaded);
        Run(hwnd, static_cast<DWORD>(pid), area == L"client", static_cast<int>(cap), GetTickCount64() + budget);
        uninit_apartment();
        return 0;
    } catch (ExitCode error) { return error; }
      catch (...) { return CaptureFailed; }
}
