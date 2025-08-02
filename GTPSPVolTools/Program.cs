using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

using GTPSPVolTools.Packing;
using PDTools.Utils;

using CommandLine;
using CommandLine.Text;

namespace GTPSPVolTools;

class Program
{
    public const string Version = "1.1.0";

    static void Main(string[] args)
    {
        Console.WriteLine("-----------------------------------------");
        Console.WriteLine($"- GTPSPVolTools {Version} by Nenkai");
        Console.WriteLine("-----------------------------------------");
        Console.WriteLine("- https://github.com/Nenkai");
        Console.WriteLine("-----------------------------------------");
        Console.WriteLine("");

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

        if (string.IsNullOrEmpty(verbs.OutputPath))
        {
            string inputFileName = Path.GetFileNameWithoutExtension(verbs.InputPath);
            verbs.OutputPath = Path.Combine(Path.GetDirectoryName(verbs.InputPath), inputFileName + "_new.VOL");
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

        if (string.IsNullOrEmpty(verbs.OutputPath))
        {
            string inputFileName = Path.GetFileNameWithoutExtension(verbs.InputPath);
            verbs.OutputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(verbs.InputPath)), $"{inputFileName}.extracted");
        }

        var volume = new Volume(verbs.InputPath);
        if (!volume.Init(verbs.SaveVolumeHeaderToc))
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

    [Option('o', "output", HelpText = "Output volume path.")]
    public string OutputPath { get; set; }
}

[Verb("unpack", HelpText = "Unpacks a volume file.")]
public class UnpackVerbs
{
    [Option('i', "input", Required = true, HelpText = "Input file volume or folder. Usually GT.VOL.")]
    public string InputPath { get; set; }

    [Option('o', "output", HelpText = "Output Folder for unpacked files.")]
    public string OutputPath { get; set; }

    [Option("save-volume-header-toc", HelpText = "Saves the decrypted volume header and toc as a 'volume_toc_header.bin' file.")]
    public bool SaveVolumeHeaderToc { get; set; }
}
