using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using McMaster.Extensions.CommandLineUtils;

namespace rssr
{
    internal static class Program
    {
        private static bool _verboseMode = false;
        
        private static int Main(string[] args)
        {
            // Setup the basics of our application 
            var app = new CommandLineApplication()
            {
                Name = "rssr",
                Description = "Really Simple Stream Ripper",
                ExtendedHelpText = $@"
=== rssr ===
Really Simple Stream Ripper
A command line tool to save a stream from the Internet to a local file.  Ideal for recording an MP3 radio show stream.
                    
Built:   {GetBuildDate(Assembly.GetExecutingAssembly()):f}
Version: {Assembly.GetExecutingAssembly().GetName().Version}

Created by Mark Young (tip2tail)
https://www.github.com/tip2tail/rssr
"
            };
            app.HelpOption(inherited: true);

            // Arguments
            var argSource = app.Argument("source stream", "The URL to the source stream").IsRequired();
            var argOutput = app.Argument("output directory", "The directory to write the output file to").IsRequired();
            
            // Options
            var optRunningTime = app.Option<int>("-r|--run-time",
                "Running time in seconds for the stream ripping session",
                CommandOptionType.SingleValue);
            var optBaseName = app.Option<string>("-b|--base-name",
                "Use the provided string as the base filename.  Date and time are added automatically",
                CommandOptionType.SingleValue);
            var optExtension = app.Option<string>("-x|--extension",
                "File name extension for the saved file.  Default is 'mp3'",
                CommandOptionType.SingleValue);
            var optVerbose = app.Option<bool>("-v|--verbose",
                "Verbose output mode",
                CommandOptionType.NoValue);

            // Processing Function
            app.OnExecute(() =>
            {
                _verboseMode = optVerbose.HasValue();

                // Execute the main meat of the application
                W("=== rssr ===", true);
                W("Stream ripping process started...", true);

                // Check the source argument
                if (string.IsNullOrEmpty(argSource.Value))
                {
                    ShowError("No source URL has been provided");
                    return ReturnCode(1);
                }
                if (!TestUrl(argSource.Value, out var tempError))
                {
                    ShowError(tempError);
                    return ReturnCode(2);
                }
                
                // Check the destination
                if (string.IsNullOrEmpty(argOutput.Value))
                {
                    ShowError("No destination directory provided");
                    return ReturnCode(3);
                }
                if (!CheckDirectory(argOutput.Value, out tempError))
                {
                    ShowError(tempError);
                    return ReturnCode(4);
                }

                // Build the output filename
                var extension = "mp3";
                if (optExtension.HasValue())
                {
                    extension = optExtension.ParsedValue.Trim('.');
                }
                W($"File extension: .{extension}");

                var baseName = "rssr-capture";
                if (optBaseName.HasValue())
                {
                    if (optBaseName.ParsedValue.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    {
                        // Invalid filename characters - fall back to base!
                        W("Invalid filename characters in passed value, reverting to default base name");
                    }
                    else
                    {
                        // OK - let's use this
                        baseName = optBaseName.ParsedValue;
                    }
                }
                
                var outputFilename = $"{baseName}-{DateTime.Now:yyyyMMdd_HHmmss}.{extension}";
                var fullOutputPath = Path.Combine(argOutput.Value, outputFilename);
                W($"Output path is: {fullOutputPath}");
                
                // Is time a factor?
                var watchTime = -1;
                if (optRunningTime.HasValue())
                {
                    watchTime = optRunningTime.ParsedValue;
                    W($"Running for {watchTime} seconds only!");
                }
                else
                {
                    W("Will run until the process is terminated externally or the source stops");
                }

                var startTime = DateTime.Now;
                W($"Starting at {startTime:HH.mm.ss}...");

                var outputFileStream = File.Create(fullOutputPath);
                var webResponse = WebRequest.Create(argSource.Value).GetResponse();
                using var webStream = webResponse.GetResponseStream();
                
                var readBuffer = new byte[65536]; // 64KB chunks
                int bytesRead;
                int chunkCount = 0;
                
                while (webStream != null && (bytesRead = webStream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    var readPos = outputFileStream.Position;
                    outputFileStream.Position = outputFileStream.Length;
                    outputFileStream.Write(readBuffer, 0, bytesRead);
                    outputFileStream.Position = readPos;
                    chunkCount++;

                    if (watchTime <= -1) continue;
                    
                    // We need to check the time, only output a message every 200 chunks
                    var timeSpan = DateTime.Now - startTime;
                    if (chunkCount % 200 == 0)
                    {
                        W($"Chunks Read: {chunkCount}");
                        W($"Seconds Running: {timeSpan.TotalSeconds}");
                    }

                    if (!(timeSpan.TotalSeconds >= watchTime)) continue;
                    W("TIME TO STOP!");
                    break;
                }
                
                // Gather & output the statistics
                W($"Stream captured successfully to: {fullOutputPath}", true);
                W($"File size: {GetFileSize(fullOutputPath)}", true);

                // All done!
                return ReturnCode(0);
            });
            
            // Execute the application
            return app.Execute(args);
        }

        /// <summary>
        /// Return the size of a file in human readable format
        /// </summary>
        /// <param name="filename">Path to the file</param>
        /// <returns>string</returns>
        private static string GetFileSize(string filename)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = new FileInfo(filename).Length;
            var order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len/1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private static bool CheckDirectory(string dir, out string tempError)
        {
            tempError = string.Empty;
            try
            {
                if (!Directory.Exists(dir))
                {
                    W($"Directory {dir} does not exist but will attempt to create it");
                    Directory.CreateDirectory(dir);
                    W("Directory created OK");
                }
                
                // Now we try and write a temp file to this directory to check write permissions
                W("Creating test file to check permissions");
                var fileName = Path.Combine(dir, "rssr_test_temp_file");
                var myFileStream = File.Open(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                myFileStream.Close();
                myFileStream.Dispose();
                File.SetLastWriteTimeUtc(fileName, DateTime.UtcNow);
                
                W("File created OK - removing file");
                File.Delete(fileName);
                W("File deleted OK");
                
                W("Output directory is all OK");
                return true;
            }
            catch (Exception exception)
            {
                tempError = "EXCEPTION: " + exception.Message;
                return false;
            }
        }

        private static bool TestUrl(string url, out string errorMessage)
        {
            errorMessage = string.Empty;
            W($"Checking format of {url}");
            if (!(Uri.TryCreate(url, UriKind.Absolute, out var uriResult) 
                  && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps)))
            {
                W("Invalid URL format!");
                errorMessage = "Invalid URL format: " + url;
                return false;
            }
            W("Format OK");
            
            W($"Testing connection to {url}");
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Timeout = 15000;
            request.Method = "HEAD";
            try
            {
                using var response = (HttpWebResponse)request.GetResponse();
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    W("Connection OK");
                    return true;
                }

                errorMessage = $"Connection failed, Status: {response.StatusCode}";
                W("Connection failed");
                return false;
            }
            catch (WebException exception)
            {
                W("Connection attempt raised exception!");
                errorMessage = "EXCEPTION: " + exception.Message;
                return false;
            }
        }

        [SuppressMessage("ReSharper", "StringIndexOfIsCultureSpecific.1")]
        private static DateTime GetBuildDate(Assembly assembly)
        {
            const string buildVersionMetadataPrefix = "+build";

            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            if (attribute?.InformationalVersion == null) return default;
            
            var value = attribute.InformationalVersion;
            var index = value.IndexOf(buildVersionMetadataPrefix);
            if (index <= 0) return default;
            
            value = value.Substring(index + buildVersionMetadataPrefix.Length);
            return DateTime.TryParseExact(value, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) ? result : default;
        }
        
        private static void ShowError(string message)
        {
            var oldBack = Console.BackgroundColor;
            var oldFore = Console.ForegroundColor;
            Console.BackgroundColor = ConsoleColor.Red;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine();
            Console.WriteLine("=== ERROR ===");
            Console.WriteLine(message);
            Console.BackgroundColor = oldBack;
            Console.ForegroundColor = oldFore;
            Console.WriteLine();
        }

        /// <summary>
        /// Wrapper for Console.WriteLine.  Will only output in verbose mode unless forced.
        /// </summary>
        /// <param name="line">The message to write to console</param>
        /// <param name="force">Force the message regardless of verbose mode</param>
        private static void W(string line, bool force = false)
        {
            if (force || _verboseMode)
            {
                Console.WriteLine(line);
            }
        }

        /// <summary>
        /// Sets the return code ensuring we use POSIX standards.
        /// </summary>
        /// <param name="code">The return code for the process</param>
        /// <returns>integer</returns>
        private static int ReturnCode(int code)
        {
            if (code == 0)
            {
                return 0;
            }
            else
            {
                return 128 + code;
            }
        }
    }
}