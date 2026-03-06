// =============================================================================
// RenderBatchWriter — Binary Patch Serializer
// =============================================================================
// Serializes a list of Patch objects into a compact binary format for
// efficient transfer to JavaScript via MemoryView (zero-copy).
//
// Binary Format:
//   Header (8 bytes):
//     PatchCount:        int32 (4 bytes) — number of patch entries
//     StringTableOffset: int32 (4 bytes) — byte offset where string table begins
//
//   Patch Entries (16 bytes each):
//     Type:  int32 (4 bytes) — BinaryPatchType enum value
//     Field1: int32 (4 bytes) — string table index (-1 = null)
//     Field2: int32 (4 bytes) — string table index (-1 = null)
//     Field3: int32 (4 bytes) — string table index (-1 = null)
//
//   String Table:
//     Sequence of LEB128-prefixed UTF-8 strings.
//     Strings are deduplicated via a Dictionary so identical values
//     (e.g., the same element ID referenced by multiple patches)
//     share a single string table slot.
//
// Design Decisions:
//   • 3 fields per patch — covers the widest patch (e.g., MoveChild: parentId,
//     childId, beforeId). Narrower patches leave trailing fields as -1.
//   • String table deduplication — element IDs are frequently repeated
//     (parent + child). Dedup reduces payload size significantly.
//   • LEB128 length encoding — compact for short strings (IDs, tag names),
//     no wasted bytes on alignment padding.
//   • Fixed-size entries — O(1) random access, trivial JS DataView parsing.
//
// See also:
//   - Interop.cs — ApplyBinaryBatch JSImport
//   - abies.js — binary reader
//   - DOM/Patch.cs — patch type definitions
// =============================================================================

using System.Buffers;
using System.Text;
using Abies.DOM;

namespace Abies;

/// <summary>
/// Binary patch type opcodes — the integer values serialized into the binary format.
/// </summary>
/// <remarks>
/// These correspond 1:1 with the Patch types defined in <see cref="Patch"/>.
/// The JS reader uses these values in a switch statement.
/// </remarks>
internal enum BinaryPatchType : int
{
    AddRoot = 0,
    ReplaceChild = 1,
    AddChild = 2,
    RemoveChild = 3,
    ClearChildren = 4,
    SetChildrenHtml = 5,
    AppendChildrenHtml = 23,
    MoveChild = 6,
    UpdateAttribute = 7,
    AddAttribute = 8,
    RemoveAttribute = 9,
    AddHandler = 10,
    RemoveHandler = 11,
    UpdateHandler = 12,
    UpdateText = 13,
    AddText = 14,
    RemoveText = 15,
    AddRaw = 16,
    RemoveRaw = 17,
    ReplaceRaw = 18,
    UpdateRaw = 19,
    AddHeadElement = 20,
    UpdateHeadElement = 21,
    RemoveHeadElement = 22
}

/// <summary>
/// Writes a list of <see cref="Patch"/> objects to a compact binary format.
/// </summary>
/// <remarks>
/// <para>
/// This class is NOT thread-safe — designed for single-threaded WASM execution.
/// Uses <see cref="ArrayBufferWriter{T}"/> for efficient contiguous byte output.
/// </para>
/// <para>
/// The string table uses <see cref="Dictionary{TKey,TValue}"/> for O(1) dedup
/// of repeated strings (element IDs, attribute names, tag names).
/// </para>
/// </remarks>
internal sealed class RenderBatchWriter
{
    // =========================================================================
    // Constants
    // =========================================================================

    private const int HeaderSize = 8;           // PatchCount + StringTableOffset
    private const int PatchEntrySize = 16;      // Type + Field1 + Field2 + Field3
    private const int NullIndex = -1;

    // =========================================================================
    // Pooled State — reused across writes
    // =========================================================================

    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private readonly Dictionary<string, int> _stringTable = new();
    private readonly List<string> _strings = [];

    // =========================================================================
    // Public API
    // =========================================================================

    /// <summary>
    /// Serializes a list of patches into binary format and returns the result
    /// as a <see cref="ReadOnlyMemory{T}"/> suitable for MemoryView transfer.
    /// </summary>
    /// <param name="patches">The patches to serialize.</param>
    /// <returns>The binary-encoded patch data.</returns>
    public ReadOnlyMemory<byte> Write(IReadOnlyList<Patch> patches)
    {
        _buffer.Clear();
        _stringTable.Clear();
        _strings.Clear();

        // Write placeholder header (will overwrite after we know stringTableOffset)
        WriteInt32ToBuffer(0);  // PatchCount placeholder
        WriteInt32ToBuffer(0);  // StringTableOffset placeholder

        // Write patch entries
        foreach (var patch in patches)
        {
            WritePatch(patch);
        }

        // Record string table offset (current position)
        var stringTableOffset = _buffer.WrittenCount;

        // Write string table
        WriteStringTable();

        // Overwrite header with actual values by getting the memory and
        // writing directly into the underlying array
        var memory = _buffer.WrittenMemory;
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(
            memory, out var segment);
        var headerSpan = segment.Array.AsSpan(segment.Offset, HeaderSize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            headerSpan[..4], patches.Count);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
            headerSpan[4..8], stringTableOffset);

        return memory;
    }

    // =========================================================================
    // Patch Entry Writing
    // =========================================================================

    private void WritePatch(Patch patch)
    {
        switch (patch)
        {
            case AddRoot p:
                WriteEntry(BinaryPatchType.AddRoot,
                    Intern(p.Element.Id),
                    Intern(Render.Html(p.Element)),
                    NullIndex);
                break;

            case ReplaceChild p:
                WriteEntry(BinaryPatchType.ReplaceChild,
                    Intern(p.OldElement.Id),
                    Intern(p.NewElement.Id),
                    Intern(Render.Html(p.NewElement)));
                break;

            case AddChild p:
                WriteEntry(BinaryPatchType.AddChild,
                    Intern(p.Parent.Id),
                    Intern(p.Child.Id),
                    Intern(Render.Html(p.Child)));
                break;

            case RemoveChild p:
                WriteEntry(BinaryPatchType.RemoveChild,
                    Intern(p.Parent.Id),
                    Intern(p.Child.Id),
                    NullIndex);
                break;

            case ClearChildren p:
                WriteEntry(BinaryPatchType.ClearChildren,
                    Intern(p.Parent.Id),
                    NullIndex,
                    NullIndex);
                break;

            case SetChildrenHtml p:
                WriteEntry(BinaryPatchType.SetChildrenHtml,
                    Intern(p.Parent.Id),
                    Intern(Render.HtmlChildren(p.Children)),
                    NullIndex);
                break;

            case AppendChildrenHtml p:
                WriteEntry(BinaryPatchType.AppendChildrenHtml,
                    Intern(p.Parent.Id),
                    Intern(Render.HtmlChildren(p.Children)),
                    NullIndex);
                break;

            case MoveChild p:
                WriteEntry(BinaryPatchType.MoveChild,
                    Intern(p.Parent.Id),
                    Intern(p.Child.Id),
                    p.BeforeId is not null ? Intern(p.BeforeId) : NullIndex);
                break;

            case UpdateAttribute p:
                WriteEntry(BinaryPatchType.UpdateAttribute,
                    Intern(p.Element.Id),
                    Intern(p.Attribute.Name),
                    Intern(p.Value));
                break;

            case AddAttribute p:
                WriteEntry(BinaryPatchType.AddAttribute,
                    Intern(p.Element.Id),
                    Intern(p.Attribute.Name),
                    Intern(p.Attribute.Value));
                break;

            case RemoveAttribute p:
                WriteEntry(BinaryPatchType.RemoveAttribute,
                    Intern(p.Element.Id),
                    Intern(p.Attribute.Name),
                    NullIndex);
                break;

            case AddHandler p:
                WriteEntry(BinaryPatchType.AddHandler,
                    Intern(p.Element.Id),
                    Intern(p.Handler.Name),
                    Intern(p.Handler.CommandId));
                break;

            case RemoveHandler p:
                WriteEntry(BinaryPatchType.RemoveHandler,
                    Intern(p.Element.Id),
                    Intern(p.Handler.Name),
                    Intern(p.Handler.CommandId));
                break;

            case UpdateHandler p:
                WriteEntry(BinaryPatchType.UpdateHandler,
                    Intern(p.Element.Id),
                    Intern(p.OldHandler.Name),
                    Intern(p.NewHandler.CommandId));
                break;

            case UpdateText p:
                WriteEntry(BinaryPatchType.UpdateText,
                    Intern(p.Parent.Id),
                    Intern(p.Text),
                    Intern(p.NewId));
                break;

            case AddText p:
                WriteEntry(BinaryPatchType.AddText,
                    Intern(p.Parent.Id),
                    Intern(p.Child.Value),
                    Intern(p.Child.Id));
                break;

            case RemoveText p:
                WriteEntry(BinaryPatchType.RemoveText,
                    Intern(p.Parent.Id),
                    Intern(p.Child.Id),
                    NullIndex);
                break;

            case AddRaw p:
                WriteEntry(BinaryPatchType.AddRaw,
                    Intern(p.Parent.Id),
                    Intern(p.Child.Html),
                    Intern(p.Child.Id));
                break;

            case RemoveRaw p:
                WriteEntry(BinaryPatchType.RemoveRaw,
                    Intern(p.Parent.Id),
                    Intern(p.Child.Id),
                    NullIndex);
                break;

            case ReplaceRaw p:
                WriteEntry(BinaryPatchType.ReplaceRaw,
                    Intern(p.OldNode.Id),
                    Intern(p.NewNode.Id),
                    Intern(p.NewNode.Html));
                break;

            case UpdateRaw p:
                WriteEntry(BinaryPatchType.UpdateRaw,
                    Intern(p.Node.Id),
                    Intern(p.Html),
                    Intern(p.NewId));
                break;

            case AddHeadElement p:
                WriteEntry(BinaryPatchType.AddHeadElement,
                    Intern(p.Content.Key),
                    Intern(p.Content.ToHtml()),
                    NullIndex);
                break;

            case UpdateHeadElement p:
                WriteEntry(BinaryPatchType.UpdateHeadElement,
                    Intern(p.Content.Key),
                    Intern(p.Content.ToHtml()),
                    NullIndex);
                break;

            case RemoveHeadElement p:
                WriteEntry(BinaryPatchType.RemoveHeadElement,
                    Intern(p.Key),
                    NullIndex,
                    NullIndex);
                break;
        }
    }

    /// <summary>
    /// Writes a single 16-byte patch entry.
    /// </summary>
    private void WriteEntry(BinaryPatchType type, int field1, int field2, int field3)
    {
        var span = _buffer.GetSpan(PatchEntrySize);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[..4], (int)type);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[4..8], field1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[8..12], field2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[12..16], field3);
        _buffer.Advance(PatchEntrySize);
    }

    // =========================================================================
    // String Table
    // =========================================================================

    /// <summary>
    /// Interns a string and returns its index in the string table.
    /// Duplicate strings share the same index (dedup).
    /// </summary>
    private int Intern(string value)
    {
        if (_stringTable.TryGetValue(value, out var index))
            return index;

        index = _strings.Count;
        _strings.Add(value);
        _stringTable[value] = index;
        return index;
    }

    /// <summary>
    /// Writes the string table: each string is prefixed with its UTF-8 byte
    /// length encoded as LEB128, followed by the UTF-8 bytes.
    /// </summary>
    private void WriteStringTable()
    {
        foreach (var str in _strings)
        {
            var byteCount = Encoding.UTF8.GetByteCount(str);

            // LEB128 encode the length (1-5 bytes for lengths up to 2^35)
            WriteLeb128(byteCount);

            // Write UTF-8 bytes
            var span = _buffer.GetSpan(byteCount);
            Encoding.UTF8.GetBytes(str.AsSpan(), span);
            _buffer.Advance(byteCount);
        }
    }

    /// <summary>
    /// Writes a non-negative integer in LEB128 (Little Endian Base 128) encoding.
    /// </summary>
    /// <remarks>
    /// LEB128 is a variable-length encoding that uses 1 byte for values 0–127,
    /// 2 bytes for 128–16383, etc. This is more compact than fixed-width int32
    /// for the typical string lengths in DOM patches (IDs: ~20 chars, HTML: varies).
    /// </remarks>
    private void WriteLeb128(int value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            if (value > 0)
                b |= 0x80;

            var span = _buffer.GetSpan(1);
            span[0] = b;
            _buffer.Advance(1);
        } while (value > 0);
    }

    // =========================================================================
    // Low-level Helpers
    // =========================================================================

    /// <summary>
    /// Writes a little-endian int32 to the buffer at the current position
    /// and advances.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void WriteInt32ToBuffer(int value)
    {
        var span = _buffer.GetSpan(4);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span, value);
        _buffer.Advance(4);
    }
}
