using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

using GTPSPUnpacker.Packing;
using PDTools.Utils;

using CommandLine;
using CommandLine.Text;

namespace GTPSPUnpacker
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("GTPSPUnpacker by Nenkai#9075");
            Console.WriteLine();

            Parser.Default.ParseArguments<PackVerbs, UnpackVerbs>(args)
                .WithParsed<PackVerbs>(Pack)
                .WithParsed<UnpackVerbs>(Unpack)
                .WithNotParsed(HandleNotParsedArgs);

            Console.WriteLine("Exiting.");
        }

        static void Pack(PackVerbs verbs)
        {
            if (!Directory.Exists(verbs.InputPath))
            {
                Console.WriteLine("ERROR: Input directory does not exist.");
                return;
            }

            var volume = new VolumeBuilder();
            volume.RegisterFilesToPack(verbs.InputPath);
            volume.Build(verbs.OutputPath);
        }

        static void Unpack(UnpackVerbs verbs)
        {
            if (!File.Exists(verbs.InputPath))
            {
                Console.WriteLine("ERROR: Input volume file does not exist.");
                return;
            }

            var volume = new Volume(verbs.InputPath);
            if (!volume.Init())
            {
                Console.WriteLine("ERROR: Could not read volume.");
                return;
            }

            Console.WriteLine("Unpacking files...");
            volume.UnpackAll(verbs.OutputPath);
        }

        static void HandleNotParsedArgs(IEnumerable<Error> errors)
        {

        }
    }

    [Verb("pack", HelpText = "Packs a folder to a volume file.")]
    public class PackVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input folder to pack as a volume. Should be the extracted game (with advertise, etc).")]
        public string InputPath { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output volume path.")]
        public string OutputPath { get; set; }
    }

    [Verb("unpack", HelpText = "Unpacks a volume file.")]
    public class UnpackVerbs
    {
        [Option('i', "input", Required = true, HelpText = "Input file volume or folder. Usually GT.VOL.")]
        public string InputPath { get; set; }

        [Option('o', "output", Required = true, HelpText = "Output Folder for unpacked files.")]
        public string OutputPath { get; set; }

        [Option("save-volume-header-toc", HelpText = "Saves the decrypted volume header and toc as a 'volume_toc_header.bin' file.")]
        public bool SaveVolumeHeaderToc { get; set; }
    }
}
