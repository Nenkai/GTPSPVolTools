using System;
using System.IO;
namespace GTPSPUnpacker
{
    class Program
    {
        static void Main(string[] args)
        {
            var volume = new Volume(args[0]);

            volume.Init();
        }
    }
}
