using System.Collections;
using System.Globalization;
using System.Management.Automation;
using CrashDrive.Provider;
using Microsoft.Diagnostics.Runtime;

namespace CrashDrive.Cmdlets;

/// <summary>
/// Inspect a managed (.NET) object on the GC heap of a Dump drive. Returns a
/// structured PSObject with TypeName, Size, and Fields[]. Reference-typed
/// fields are shown as addresses (follow with another Get-CrashObject call).
///
/// This is ClrMD-based and therefore Dump-only. TTD object inspection requires
/// loading SOS and has a different plumbing path.
/// </summary>
[Cmdlet(VerbsCommon.Get, "CrashObject")]
[OutputType(typeof(PSObject))]
public sealed class GetCrashObjectCmdlet : PSCmdlet
{
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    public string Address { get; set; } = "";

    [Parameter]
    public string? Drive { get; set; }

    /// <summary>Also inline the fields of reference-typed members one level deep.</summary>
    [Parameter]
    public SwitchParameter Expand { get; set; }

    protected override void ProcessRecord()
    {
        var addr = ParseAddress(Address);
        if (addr == null)
        {
            WriteError(new ErrorRecord(
                new ArgumentException($"Invalid address: {Address}"),
                "BadAddress", ErrorCategory.InvalidArgument, Address));
            return;
        }

        var drive = ResolveDumpDrive();
        if (drive == null) return;
        var runtime = drive.Store.ClrRuntime;
        if (runtime == null)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException("No CLR runtime in this dump."),
                "NoClr", ErrorCategory.InvalidOperation, drive));
            return;
        }

        var obj = runtime.Heap.GetObject(addr.Value);
        if (obj.Type == null)
        {
            WriteError(new ErrorRecord(
                new InvalidOperationException($"No object at 0x{addr.Value:X} (not on heap, or type unresolved)."),
                "NotAnObject", ErrorCategory.ObjectNotFound, Address));
            return;
        }

        WriteObject(Render(obj, Expand.IsPresent));
    }

    private static PSObject Render(ClrObject obj, bool expand)
    {
        var ps = new PSObject();
        ps.Properties.Add(new PSNoteProperty("Address", $"0x{obj.Address:X}"));
        ps.Properties.Add(new PSNoteProperty("TypeName", obj.Type!.Name));
        ps.Properties.Add(new PSNoteProperty("Size", (long)obj.Size));
        ps.Properties.Add(new PSNoteProperty("MethodTable", $"0x{obj.Type.MethodTable:X}"));

        // Strings have a direct value.
        if (obj.Type.IsString)
        {
            var str = obj.AsString(2048);
            ps.Properties.Add(new PSNoteProperty("Value", str ?? ""));
            return ps;
        }

        // Arrays: element type, length, first elements.
        if (obj.IsArray)
        {
            var arr = obj.AsArray();
            ps.Properties.Add(new PSNoteProperty("Length", arr.Length));
            var componentType = obj.Type.ComponentType?.Name ?? "?";
            ps.Properties.Add(new PSNoteProperty("ElementType", componentType));
            var items = new List<object?>();
            var take = Math.Min(arr.Length, 16);
            for (int i = 0; i < take; i++)
            {
                try { items.Add(RenderArrayElement(arr, i)); }
                catch { items.Add(null); }
            }
            ps.Properties.Add(new PSNoteProperty("Preview", items));
            return ps;
        }

        // Regular reference type: enumerate instance fields.
        var fields = new List<PSObject>();
        foreach (var f in obj.Type.Fields)
        {
            var field = new PSObject();
            field.Properties.Add(new PSNoteProperty("Name", f.Name ?? "?"));
            field.Properties.Add(new PSNoteProperty("TypeName", f.Type?.Name ?? "?"));
            try { field.Properties.Add(new PSNoteProperty("Value", RenderFieldValue(obj, f, expand))); }
            catch (Exception ex) { field.Properties.Add(new PSNoteProperty("Value", $"<err: {ex.Message}>")); }
            fields.Add(field);
        }
        ps.Properties.Add(new PSNoteProperty("Fields", fields));
        return ps;
    }

    private static object? RenderFieldValue(ClrObject obj, ClrInstanceField f, bool expand)
    {
        var ft = f.Type;
        if (ft == null) return null;

        if (ft.IsString)
        {
            return f.ReadString(obj.Address, interior: false);
        }
        if (ft.IsObjectReference)
        {
            var child = f.ReadObject(obj.Address, interior: false);
            if (child.IsNull) return "null";
            var addrStr = $"0x{child.Address:X} <{child.Type?.Name ?? "?"}>";
            if (!expand) return addrStr;
            try { return Render(child, expand: false); } catch { return addrStr; }
        }
        // Primitive / value type: use the generic ReadField<T>. ClrMD's API
        // takes a concrete type parameter, so dispatch on ElementType.
        return f.ElementType switch
        {
            ClrElementType.Boolean => f.Read<bool>(obj.Address, interior: false),
            ClrElementType.Char    => f.Read<char>(obj.Address, interior: false),
            ClrElementType.Int8    => f.Read<sbyte>(obj.Address, interior: false),
            ClrElementType.UInt8   => f.Read<byte>(obj.Address, interior: false),
            ClrElementType.Int16   => f.Read<short>(obj.Address, interior: false),
            ClrElementType.UInt16  => f.Read<ushort>(obj.Address, interior: false),
            ClrElementType.Int32   => f.Read<int>(obj.Address, interior: false),
            ClrElementType.UInt32  => f.Read<uint>(obj.Address, interior: false),
            ClrElementType.Int64   => f.Read<long>(obj.Address, interior: false),
            ClrElementType.UInt64  => f.Read<ulong>(obj.Address, interior: false),
            ClrElementType.Float   => f.Read<float>(obj.Address, interior: false),
            ClrElementType.Double  => f.Read<double>(obj.Address, interior: false),
            ClrElementType.NativeInt or ClrElementType.Pointer or ClrElementType.FunctionPointer
                => $"0x{f.Read<ulong>(obj.Address, interior: false):X}",
            ClrElementType.Struct  => "<struct>",
            _ => $"<{ft.Name}>",
        };
    }

    private static object? RenderArrayElement(ClrArray arr, int index)
    {
        var ct = arr.Type.ComponentType;
        if (ct == null) return null;
        if (ct.IsObjectReference)
        {
            var el = arr.GetObjectValue(index);
            return el.IsNull ? "null" : $"0x{el.Address:X} <{el.Type?.Name ?? "?"}>";
        }
        return ct.ElementType switch
        {
            ClrElementType.Boolean => arr.GetValue<bool>(index),
            ClrElementType.Char    => arr.GetValue<char>(index),
            ClrElementType.Int8    => arr.GetValue<sbyte>(index),
            ClrElementType.UInt8   => arr.GetValue<byte>(index),
            ClrElementType.Int16   => arr.GetValue<short>(index),
            ClrElementType.UInt16  => arr.GetValue<ushort>(index),
            ClrElementType.Int32   => arr.GetValue<int>(index),
            ClrElementType.UInt32  => arr.GetValue<uint>(index),
            ClrElementType.Int64   => arr.GetValue<long>(index),
            ClrElementType.UInt64  => arr.GetValue<ulong>(index),
            ClrElementType.Float   => arr.GetValue<float>(index),
            ClrElementType.Double  => arr.GetValue<double>(index),
            _ => $"<{ct.Name}>",
        };
    }

    private DumpDriveInfo? ResolveDumpDrive()
    {
        PSDriveInfo? d;
        if (!string.IsNullOrEmpty(Drive))
        {
            d = SessionState.Drive.Get(Drive);
        }
        else
        {
            d = SessionState.Path.CurrentLocation.Drive;
        }
        if (d is DumpDriveInfo dmp) return dmp;
        WriteError(new ErrorRecord(
            new InvalidOperationException($"Drive '{d?.Name ?? "?"}' is not a CrashDrive Dump drive."),
            "UnsupportedDrive", ErrorCategory.InvalidOperation, d));
        return null;
    }

    private static ulong? ParseAddress(string s)
    {
        s = s.Trim().Replace("`", "").Replace("_", "");
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
            return hex;
        if (ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
            return dec;
        return null;
    }
}
