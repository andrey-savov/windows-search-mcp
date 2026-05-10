using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WsSearchMcp;

public sealed record PropertyInfo(
    string CanonicalName,
    string? DisplayName,
    string Type);

[SupportedOSPlatform("windows")]
internal static class WindowsPropertySystem
{
    private static readonly Lazy<IReadOnlyList<PropertyInfo>> _cache =
        new(() => EnumerateInternal(), isThreadSafe: true);

    public static IReadOnlyList<PropertyInfo> All => _cache.Value;

    public static IEnumerable<PropertyInfo> ByFamily(string family)
    {
        if (string.IsNullOrWhiteSpace(family) || family.Equals("System", StringComparison.OrdinalIgnoreCase))
        {
            // Root: properties like "System.X" with no second dot segment.
            return All.Where(p =>
            {
                if (!p.CanonicalName.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) return false;
                var rest = p.CanonicalName["System.".Length..];
                return !rest.Contains('.');
            });
        }

        var prefix = $"System.{family}.";
        return All.Where(p =>
            p.CanonicalName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public static IEnumerable<PropertyInfo> Search(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return All;
        return All.Where(p =>
            p.CanonicalName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            (p.DisplayName is not null &&
             p.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<PropertyInfo> EnumerateInternal()
    {
        var iidList = typeof(IPropertyDescriptionList).GUID;
        var iidDesc = typeof(IPropertyDescription).GUID;

        PSEnumeratePropertyDescriptions(PROPDESC_ENUMFILTER.PDEF_ALL, ref iidList, out var listObj);
        var list = (IPropertyDescriptionList)listObj;
        var count = list.GetCount();

        var results = new List<PropertyInfo>((int)count);
        for (uint i = 0; i < count; i++)
        {
            int hr = list.GetAt(i, ref iidDesc, out var descObj);
            if (hr != 0 || descObj is null) continue;
            var desc = (IPropertyDescription)descObj;
            try
            {
                string canonical;
                if (desc.GetCanonicalName(out canonical!) != 0 || string.IsNullOrEmpty(canonical))
                    continue;

                string? display = null;
                if (desc.GetDisplayName(out var dn) == 0 && !string.IsNullOrEmpty(dn))
                    display = dn;

                string typeName = "Unknown";
                if (desc.GetPropertyType(out var vt) == 0)
                    typeName = VarTypeName(vt);

                results.Add(new PropertyInfo(canonical, display, typeName));
            }
            finally
            {
                Marshal.ReleaseComObject(desc);
            }
        }
        Marshal.ReleaseComObject(list);

        results.Sort((a, b) => string.Compare(a.CanonicalName, b.CanonicalName, StringComparison.OrdinalIgnoreCase));
        return results;
    }

    private static string VarTypeName(ushort vt)
    {
        const ushort VT_VECTOR = 0x1000;
        var isVector = (vt & VT_VECTOR) != 0;
        var baseVt = (ushort)(vt & ~VT_VECTOR);
        var baseName = baseVt switch
        {
            0 => "Empty",
            1 => "Null",
            2 => "Int16",
            3 => "Int32",
            4 => "Single",
            5 => "Double",
            6 => "Currency",
            7 => "Date",
            8 => "BSTR",
            11 => "Boolean",
            16 => "SByte",
            17 => "Byte",
            18 => "UInt16",
            19 => "UInt32",
            20 => "Int64",
            21 => "UInt64",
            30 => "LPSTR",
            31 => "String",   // VT_LPWSTR
            64 => "DateTime", // VT_FILETIME
            72 => "Guid",     // VT_CLSID
            _ => $"VARTYPE({baseVt})"
        };
        return isVector ? baseName + "[]" : baseName;
    }

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void PSEnumeratePropertyDescriptions(
        PROPDESC_ENUMFILTER filterOn,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out object ppv);

    private enum PROPDESC_ENUMFILTER : uint
    {
        PDEF_ALL = 0,
        PDEF_SYSTEM = 1,
        PDEF_NONSYSTEM = 2,
        PDEF_VIEWABLE = 3,
        PDEF_QUERYABLE = 4,
        PDEF_INFULLTEXTQUERY = 5,
        PDEF_COLUMN = 6,
    }

    [ComImport]
    [Guid("1F9FC1D0-C39B-4B26-817F-011967D3440E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyDescriptionList
    {
        uint GetCount();
        [PreserveSig]
        int GetAt(uint iElem, [In] ref Guid riid,
            [Out, MarshalAs(UnmanagedType.Interface)] out object? ppv);
    }

    [ComImport]
    [Guid("6F79D558-3E96-4549-A1D1-7D75D2288814")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyDescription
    {
        [PreserveSig] int GetPropertyKey(out PROPERTYKEY pkey);
        [PreserveSig] int GetCanonicalName([MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
        [PreserveSig] int GetPropertyType(out ushort pvartype);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string? ppszName);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }
}
