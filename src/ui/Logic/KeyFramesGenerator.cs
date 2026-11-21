using Nikse.SubtitleEdit.Core.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Nikse.SubtitleEdit.Logic
{
    public static class KeyFramesGenerator
    {
        public static List<double> GetKeyFrames(string videoFileName)
        {
            var keyFrames = new List<double>();
            if (string.IsNullOrWhiteSpace(videoFileName) || !File.Exists(videoFileName))
            {
                return keyFrames;
            }

            var ffProbePath = GetFfProbePath();
            if (string.IsNullOrWhiteSpace(ffProbePath))
            {
                return keyFrames;
            }

            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = ffProbePath;
                    process.StartInfo.Arguments = "-v error -skip_frame nokey -select_streams v:0 " +
                                                  "-show_frames -show_entries frame=best_effort_timestamp_time,pkt_pts_time,pkt_dts_time " +
                                                  "-of csv=p=0 " + Quote(videoFileName);
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.CreateNoWindow = true;
                    process.Start();

                    while (!process.StandardOutput.EndOfStream)
                    {
                        AddKeyFrameFromLine(process.StandardOutput.ReadLine(), keyFrames);
                    }

                    process.WaitForExit(30_000);
                }
            }
            catch
            {
                return new List<double>();
            }

            return keyFrames
                .Where(p => p >= 0)
                .Distinct()
                .OrderBy(p => p)
                .ToList();
        }

        private static string GetFfProbePath()
        {
            var ffMpegLocation = Configuration.Settings.General.FFmpegLocation;
            if (!string.IsNullOrWhiteSpace(ffMpegLocation))
            {
                var ffProbePath = Path.Combine(Path.GetDirectoryName(ffMpegLocation), "ffprobe.exe");
                if (File.Exists(ffProbePath))
                {
                    return ffProbePath;
                }
            }

            return Configuration.IsRunningOnWindows ? "ffprobe.exe" : "ffprobe";
        }

        private static string Quote(string fileName)
        {
            return "\"" + fileName.Replace("\"", "\\\"") + "\"";
        }

        private static void AddKeyFrameFromLine(string line, List<double> keyFrames)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            foreach (var part in line.Split(','))
            {
                var value = part.Trim();
                if (value.Length == 0 || value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                {
                    keyFrames.Add(seconds);
                    return;
                }
            }
        }
    }
}
