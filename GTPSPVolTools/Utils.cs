using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.IO.Compression;

using Syroot.BinaryData;
using Syroot.BinaryData.Core;
using Syroot.BinaryData.Memory;

namespace GTPSPVolTools;

public static class Utils
{
    // https://stackoverflow.com/a/4975942
    private static string[] sizesuf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
    public static string BytesToString(long byteCount)
    {
        if (byteCount == 0)
            return "0" + sizesuf[0];
        long bytes = Math.Abs(byteCount);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return (Math.Sign(byteCount) * num).ToString() + sizesuf[place];
    }

    public static int MeasureBytesTakenByBits(double bitCount)
        => (int)Math.Round(bitCount / 8, MidpointRounding.AwayFromZero);

    public static ulong GetMaxValueForBitCount(int nBits)
        => (ulong)((1L << nBits) - 1);
}
