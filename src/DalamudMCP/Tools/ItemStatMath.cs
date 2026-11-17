using System;

namespace DalamudMCP.Tools;

internal static class ItemStatMath
{
    internal static int? Cap(int? levelValue, int? slotPermille, int? rolePercent)
    {
        if (!levelValue.HasValue || !slotPermille.HasValue || !rolePercent.HasValue ||
            levelValue<0 || slotPermille<=0 || rolePercent<=0) return null;
        return checked((int)Math.Round(levelValue.Value*(double)slotPermille.Value*rolePercent.Value/100000.0,MidpointRounding.AwayFromZero));
    }

    internal static (int? EffectiveMateria,int? WastedMateria,int? Total) Apply(int innate, int materia, int? cap)
    {
        if (materia==0) return (0,0,innate);
        if (!cap.HasValue || innate>cap.Value) return (null,null,null);
        var effective = Math.Min(materia,Math.Max(0,cap.Value-innate));
        return (effective,materia-effective,innate+effective);
    }
}
