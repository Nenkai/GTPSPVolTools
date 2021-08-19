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

namespace GTPSPUnpacker
{
    public static class Utils
    {
        /// <summary>
        /// Decompresses a file (in memory, unsuited for large files).
        /// </summary>
        /// <param name="data"></param>
        /// <param name="outSize"></param>
        /// <param name="deflatedData"></param>
        /// <returns></returns>
        public unsafe static bool TryInflateInMemory(Span<byte> data, ulong outSize, out byte[] deflatedData)
        {
            deflatedData = Array.Empty<byte>();
            if (outSize > uint.MaxValue)
                return false;

            // Inflated is always little
            var sr = new SpanReader(data, Endian.Little);
            uint zlibMagic = sr.ReadUInt32();
            uint sizeComplement = sr.ReadUInt32();

            if ((long)zlibMagic != 0xFFF7EEC5)
                return false;

            if ((uint)outSize + sizeComplement != 0)
                return false;

            const int headerSize = 8;
            if (sr.Length <= headerSize) // Header size, if it's under, data is missing
                return false;

            deflatedData = new byte[(int)outSize];
            fixed (byte* pBuffer = &sr.Span.Slice(headerSize)[0]) // Vol Header Size
            {
                using var ums = new UnmanagedMemoryStream(pBuffer, sr.Span.Length - headerSize);
                using var ds = new DeflateStream(ums, CompressionMode.Decompress);
                ds.Read(deflatedData, 0, (int)outSize);
            }

            return true;
        }
    }
}
