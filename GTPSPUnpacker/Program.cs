using System;
using System.IO;

using PDTools.Utils;

namespace GTPSPUnpacker
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("GTPSPUnpacker by Nenkai#9075");
            Console.WriteLine();

            if (args.Length != 2)
            {
                Console.WriteLine("No arguments provided.");
                Console.WriteLine("GTPSPUnpacker <input volume file> <output directory>");
                return;
            }

            if (!File.Exists(args[0]))
            {
                Console.WriteLine("Volume file does not exist");
                return;
            }

            var volume = new Volume(args[0]);

            volume.Init();
            volume.UnpackAll(args[1]);
        }
    }
}
