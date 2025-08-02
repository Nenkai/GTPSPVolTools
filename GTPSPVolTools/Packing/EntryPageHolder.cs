using PDTools.Utils;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace GTPSPVolTools.Packing;

public class EntryPageHolder : PageHolderBase
{
    public List<VolumeEntry> Entries { get; set; } = [];
    public int EntriesInfoSize { get; set; }

    public bool TryAddEntry(VolumeEntry entry)
    {
        uint keySize = entry.GetSerializedSize();

        int currentSizeTaken = Utils.MeasureBytesTakenByBits(12 + ((Entries.Count - 1) * 12)); // 12 bits header, 12 bits for each entry offset (except first)
        currentSizeTaken += EntriesInfoSize; // current size of all entries in this page

        if (currentSizeTaken + (keySize + 2) > VolumeBuilder.BTREE_MAX_PAGE_SIZE) // Extra 2 to fit the 12 bits offset as short
            return false; // Entry cannot be written, exit loop to create new page

        Entries.Add(entry);
        EntriesInfoSize += (int)keySize;
        return true;
    }

    public override void Write(ref BitStream stream)
    {
        BitStream entryWriter = new BitStream(endian: BitStreamSignificantBitOrder.MSB);
        List<int> entryOffsets = [];

        for (int i = 0; i < Entries.Count; i++)
        {
            if (i != 0)
                entryOffsets.Add(entryWriter.Position);

            Entries[i].Serialize(ref entryWriter);
        }

        Debug.Assert((ulong)Entries.Count < Utils.GetMaxValueForBitCount(11),
            $"EntryPage: Entry Count was larger that could fit 11 bits ({Entries.Count} < {Utils.GetMaxValueForBitCount(11)})");

        stream.WriteBoolBit(false); // Index Block
        stream.WriteBits((ulong)Entries.Count, 11);

        for (int i = 0; i < entryOffsets.Count; i++)
        {
            uint tocSize = (uint)Utils.MeasureBytesTakenByBits(12 + (entryOffsets.Count * 12));
            Debug.Assert((ulong)(tocSize + entryOffsets[i]) < Utils.GetMaxValueForBitCount(12),
                $"EntryPage: Entry offset was larger that could fit 12 bits ({(ulong)(tocSize + entryOffsets[i])} < {Utils.GetMaxValueForBitCount(12)})");
            stream.WriteBits((ulong)(tocSize + entryOffsets[i]), 12);
        }

        stream.WriteByteData(entryWriter.GetSpan());
        stream.Align(VolumeBuilder.BLOCK_SIZE);
    }
}
