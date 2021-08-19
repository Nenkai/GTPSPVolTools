using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.IO;
namespace GTPSPUnpacker.Packing
{
    public class VolumeBuilder
    {
        public string InputFolder { get; set; }

        /// <summary>
        /// Current node ID.
        /// </summary>
        public int CurrentID = 1;

        public void RegisterFilesToPack(string inputFolder)
        {
            Console.WriteLine($"Indexing '{Path.GetFullPath(inputFolder)}' to find files to pack.. ");
            InputFolder = Path.GetFullPath(inputFolder);

            var root = new VolumeEntry();


            Import(root, InputFolder);
        }

        /// <summary>
        /// Imports a local file directory as a game directory node.
        /// </summary>
        /// <param name="parent"></param>
        /// <param name="folder"></param>
        private void Import(VolumeEntry parent, string folder)
        {
            var dirEntries = Directory.EnumerateFileSystemEntries(folder)
                .OrderBy(e => e, StringComparer.Ordinal).ToList();

            foreach (var path in dirEntries)
            {
                VolumeEntry entry;

                string relativePath = path.Substring(folder.Length + 1);
                if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                {
                    entry = new VolumeEntry();
                    entry.Type = VolumeEntry.EntryType.Directory;
                    entry.SubDirIndex = CurrentID++;
                    Import(entry, path);
                }
                else
                {
                    string absolutePath = Path.Combine(folder, relativePath);
                    string volumePath = absolutePath.Substring(InputFolder.Length + 1);

                    var fInfo = new FileInfo(absolutePath);

                    entry = new VolumeEntry();
                    entry.Type = VolumeEntry.EntryType.File;
                    /*
                    if (Compress && IsNormallyCompressedVolumeFile(volumePath))
                    {
                        entry = new CompressedFileEntry(relativePath);
                        ((CompressedFileEntry)entry).Size = (int)fInfo.Length;
                        ((CompressedFileEntry)entry).ModifiedDate = fInfo.LastWriteTimeUtc;
                    }
                    else
                    {
                        entry = new FileEntry(relativePath);
                        ((FileEntry)entry).Size = (int)fInfo.Length;
                        ((FileEntry)entry).ModifiedDate = fInfo.LastWriteTimeUtc;
                    }
                    */
                    entry.SubDirIndex = CurrentID++;
                }

                //entry.ParentNode = parent.NodeID;
                parent.Child.Add(entry);
            }
        }
    }
}
