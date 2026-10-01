namespace BondTools.Runtime;

/// <summary>A Bond wire type (<c>BondDataType</c> in upstream Bond).</summary>
public enum WireType : byte
{
    Stop = 0,
    StopBase = 1,
    Bool = 2,
    UInt8 = 3,
    UInt16 = 4,
    UInt32 = 5,
    UInt64 = 6,
    Float = 7,
    Double = 8,
    String = 9,
    Struct = 10,
    List = 11,
    Set = 12,
    Map = 13,
    Int8 = 14,
    Int16 = 15,
    Int32 = 16,
    Int64 = 17,
    WString = 18
}
