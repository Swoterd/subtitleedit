using Nikse.SubtitleEdit.Core.Common;
using System.IO;
using System.Windows.Forms;

namespace Nikse.SubtitleEdit.Logic
{
    internal static class OpenFileDialogExtensions
    {
        public static DialogResult ShowDialogWithLastFolder(this OpenFileDialog dialog, string operation, IWin32Window owner = null)
        {
            var folders = Configuration.Settings.OpenFileDialogFolders;
            var initialDirectory = dialog.InitialDirectory;
            var initialFileName = dialog.FileName;
            var result = DialogResult.None;

            try
            {
                if (folders.TryGetValue(operation, out var folder) && Directory.Exists(folder))
                {
                    dialog.InitialDirectory = folder;
                    // A full path in FileName can take precedence over InitialDirectory.
                    dialog.FileName = Path.GetFileName(initialFileName);
                }

                result = dialog.ShowDialog(owner);
                if (result == DialogResult.OK)
                {
                    var selectedFolder = Path.GetDirectoryName(dialog.FileName);
                    if (!string.IsNullOrEmpty(selectedFolder))
                    {
                        folders[operation] = selectedFolder;
                    }
                }

                return result;
            }
            finally
            {
                // Several commands reuse the same dialog instance.
                dialog.InitialDirectory = initialDirectory;
                if (result != DialogResult.OK)
                {
                    dialog.FileName = initialFileName;
                }
            }
        }
    }
}
