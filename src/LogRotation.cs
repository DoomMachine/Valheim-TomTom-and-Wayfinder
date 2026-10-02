using System;
using System.IO;
using System.Threading;

namespace Waypointer
{
    /// <summary>
    /// The once-a-session rotation of the plugin's log: "&lt;edition&gt;.log" becomes "&lt;edition&gt;-prev.log". Unity-free,
    /// so tests/ runs it on a temporary folder. The last session's file is moved aside FIRST - on Windows that move
    /// fails, and changes nothing, while another program holds the file without delete sharing (another copy of the
    /// game writing it, a reader that keeps it open) - and only then is the older "-prev" replaced. File.Move has no
    /// overwrite overload on the game's Mono or on .NET Framework 4.x, hence delete-then-move, and only in this order.
    /// Never throws.
    /// </summary>
    internal static class LogRotation
    {
        private static readonly int[] WaitsMs = { 10, 25, 50 };

        internal enum Outcome { NothingToRotate, Rotated, PlainHeld, PrevKept, StageKept, Failed }

        /// <param name="plainMode">Create when "&lt;edition&gt;.log" is free to start empty; Append when it still holds
        /// the last session's lines, which are never emptied.</param>
        internal static Outcome Rotate(string folder, string edition, out FileMode plainMode)
        {
            try
            {
                plainMode = FileMode.Append;                  // the safe answer whenever anything below is unsure
                string plain = Path.Combine(folder, LogRules.FileName(edition, false));
                string prev = Path.Combine(folder, LogRules.FileName(edition, true));
                string stage = Path.Combine(folder, LogRules.StageName(edition));

                // 0. A rotation stopped between its steps left an earlier session's file under the stage name. With a
                // plain file beside it, the plain one is newer and becomes "-prev" below; without one, the stage is.
                if (File.Exists(stage))
                {
                    try
                    {
                        if (File.Exists(plain)) File.Delete(stage);
                        else
                        {
                            File.Delete(prev);
                            File.Move(stage, prev);
                        }
                    }
                    catch (Exception) { }
                }
                if (!File.Exists(plain))
                {
                    plainMode = FileMode.Create;
                    return Outcome.NothingToRotate;
                }

                // 1. The probe: move the last session's file aside. Refused -> nothing touched, "-prev" intact.
                bool aside = false;
                for (int a = 0; !aside; a++)
                {
                    try
                    {
                        File.Move(plain, stage);
                        aside = true;
                    }
                    catch (IOException) { if (a >= WaitsMs.Length) break; Thread.Sleep(WaitsMs[a]); }
                    catch (UnauthorizedAccessException) { if (a >= WaitsMs.Length) break; Thread.Sleep(WaitsMs[a]); }
                }
                if (!aside) return Outcome.PlainHeld;         // Append: if a writer holds it, that open fails -> .1

                // 2. Only now replace "-prev" (deleting a "-prev" that is not there is not an error).
                bool replaced = false;
                for (int a = 0; !replaced; a++)
                {
                    try
                    {
                        File.Delete(prev);
                        File.Move(stage, prev);
                        replaced = true;
                    }
                    catch (IOException) { if (a >= WaitsMs.Length) break; Thread.Sleep(WaitsMs[a]); }
                    catch (UnauthorizedAccessException) { if (a >= WaitsMs.Length) break; Thread.Sleep(WaitsMs[a]); }
                }
                if (replaced)
                {
                    // The numbered files of a second copy of the game are left as they are: they are appended to, and
                    // the player removes them.
                    plainMode = FileMode.Create;
                    return Outcome.Rotated;
                }

                // 3. "-prev" is held or read-only: put the last session's file back; this session appends after it.
                try
                {
                    File.Move(stage, plain);
                    return Outcome.PrevKept;
                }
                catch (Exception)
                {
                    plainMode = FileMode.Create;
                    return Outcome.StageKept;                 // the last session's lines are safe under the stage name
                }
            }
            catch (Exception)
            {
                plainMode = FileMode.Append;
                return Outcome.Failed;
            }
        }
    }
}
