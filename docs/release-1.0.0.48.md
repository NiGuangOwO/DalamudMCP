## 简体中文

- `select_addon_menu_item` 支持 `SelectIconString` 原生图标菜单（如木人歼灭战研究者菜单）。可按标签或从零开始的菜单序号选择；干运行会校验所属窗口、可用状态和唯一的内嵌监听器。选中后最多等四次框架更新，派发前重新核对列表与标签。
- 验证：Debug 和 Release 构建通过；原生 UI 259 项、插件加载 227 项、协议 88 项通过。国服真机核对运行 MVID 后，从未选中状态一次调用即由研究者的「开战！」进入木人歼灭战挑战列表。
- 限制：结果表示输入已派发，调用方仍须读取后续界面或游戏状态确认结果；其它窗口仍需已知的回调约定。

## English

- `select_addon_menu_item` supports native `SelectIconString` icon menus such as the Stone, Sky, Sea researcher's menu. Select by label or zero-based menu ordinal. Dry runs verify ownership, availability and the unique embedded listener. The tool waits up to four framework updates after selection and rechecks the list and label before dispatch.
- Validation: Debug and Release builds passed, along with 259 native UI, 227 plugin load and 88 protocol checks. After verifying the running MVID on the CN client, one call selected the researcher's Start option from an unselected menu and opened the Stone, Sky, Sea challenge list.
- Limitations: the result reports input dispatch. Read the next window or game state to verify the outcome. Other addon layouts still require a known callback contract.
