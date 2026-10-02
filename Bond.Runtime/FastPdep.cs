using System.Runtime.Intrinsics.X86;

namespace BondTools.Runtime;

/// <summary>Whether varints of three or more bytes are encoded and decoded with pdep and pext.</summary>
/// <remarks>
/// They take a few cycles wherever BMI2 is, except on AMD families 15h to 18h (Excavator to Zen 2, and Hygon), which
/// run them in microcode at up to hundreds of cycles.
/// </remarks>
internal static class FastPdep
{
    public static readonly bool IsSupported = Bmi2.X64.IsSupported && Family() is < 0x15 or >= 0x19;

    private static uint Family()
    {
        var signature = (uint)X86Base.CpuId(1, 0).Eax;
        var family = (signature >> 8) & 0xF;
        return family == 0xF ? family + ((signature >> 20) & 0xFF) : family;
    }
}
