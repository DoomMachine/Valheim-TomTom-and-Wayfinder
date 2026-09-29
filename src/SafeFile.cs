using System;
using System.IO;
using System.Text;

namespace Waypointer
{
    /// <summary>
    /// Replaces a small file crash-safely: once the file exists, if the game crashes during a save - or the
    /// power fails and the drive honours the flush to disk - a complete copy of either the old or the new text
    /// is on disk - the pattern of the game's own
    /// FileHelpers.ReplaceOldFile, which saves the player profile. (The first save of a file has nothing older
    /// to protect: a power cut during it can leave a partial path.new.) A copy left by an interrupted save is
    /// only ever renamed back, never rewritten.
    /// Unity-free, so the tests exercise it directly.
    /// </summary>
    internal static class SafeFile
    {
        internal const string NewSuffix = ".new";
        internal const string OldSuffix = ".old";

        /// <summary>
        /// Where the current file steps aside instead of .old, when a .old left by an earlier save cannot be deleted
        /// because another program (a backup, sync or antivirus tool) holds it open.
        /// </summary>
        internal const string SpareSuffix = ".old2";

        /// <summary>
        /// Writes text to path.new and flushes it to disk; only then does the current file step aside as
        /// path.old, path.new take its name, and path.old go. If path is missing because an earlier save was
        /// cut short, its surviving copy is renamed back first, so once the file exists, path.new is only ever
        /// opened for writing while a complete path exists. A read-only path is refused, as a plain File.WriteAllText would refuse
        /// it, so the caller's retry and logging still apply; the .new and .old copies are the mod's own and
        /// their read-only flag is cleared.
        /// </summary>
        internal static void WriteAllText(string path, string text)
        {
            string fresh = path + NewSuffix;
            string previous = path + OldSuffix;
            string spare = path + SpareSuffix;

            if (!File.Exists(path))
            {
                string survivor = ReadablePath(path);
                if (survivor != null)
                {
                    // Throws if it cannot be renamed (e.g. held open by a backup tool): better to retry later
                    // than to overwrite what may be the only complete copy.
                    ClearReadOnly(survivor);
                    File.Move(survivor, path);
                }
            }

            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("Access to the path \"" + path + "\" is denied: the file is read-only.");

            ClearReadOnly(fresh);
            ClearReadOnly(previous);
            ClearReadOnly(spare);

            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (FileStream fs = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);   // to the disk itself, not just the OS cache, before the current file is touched
            }

            if (File.Exists(path))
            {
                // The current file steps aside as .old. A .old left by an earlier save is deleted first; when another
                // program holds it open, the current file steps aside as .old2 instead, rather than the save waiting
                // for that program to let go. (If .old2 is held too, this throws and the caller retries later.)
                string aside = previous;
                if (!TryDelete(previous))
                {
                    aside = spare;
                    if (File.Exists(spare)) File.Delete(spare);
                }
                File.Move(path, aside);
            }
            File.Move(fresh, path);

            // The new file is in place; the previous copies are only clutter now. Failing to remove one is harmless:
            // the next save removes it first.
            TryDelete(previous);
            TryDelete(spare);
        }

        /// <summary>Deletes the file if it is there; false, without throwing, when another program keeps it.</summary>
        private static bool TryDelete(string file)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// The file that holds the latest complete text for path: path itself; or, when a save was cut short
        /// after the current file had stepped aside, the new copy (path.new - complete, because once path exists
        /// .new is only written while it does, and it is flushed before anything is moved), else the previous one
        /// (path.old2, then path.old: a .old2 is made only while a .old is held by another program, so it holds the
        /// later text of the two; a save never leaves either without path or path.new beside it).
        /// Null when there is nothing. A path.new beside an existing path is an unfinished save and is ignored.
        /// </summary>
        internal static string ReadablePath(string path)
        {
            if (File.Exists(path)) return path;
            if (File.Exists(path + NewSuffix)) return path + NewSuffix;
            if (File.Exists(path + SpareSuffix)) return path + SpareSuffix;
            if (File.Exists(path + OldSuffix)) return path + OldSuffix;
            return null;
        }

        /// <summary>
        /// Before reading path: if it is missing because a save was cut short, renames the surviving copy back
        /// to path. Returns the file to read - path, or the surviving copy itself when it cannot be renamed right
        /// now (it is then read where it is and left untouched) - or null when there is nothing. Never throws.
        /// </summary>
        internal static string RecoverInterrupted(string path)
        {
            string survivor = ReadablePath(path);
            if (survivor == null || survivor == path) return survivor;
            try
            {
                ClearReadOnly(survivor);
                File.Move(survivor, path);
                return path;
            }
            catch (IOException) { return survivor; }
            catch (UnauthorizedAccessException) { return survivor; }
        }

        private static void ClearReadOnly(string file)
        {
            if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
    }
}
