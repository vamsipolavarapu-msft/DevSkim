// Copyright (C) Microsoft. All rights reserved. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using Microsoft.CST.RecursiveExtractor;
using Microsoft.DevSkim.CLI.Writers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CommandLine;
using KellermanSoftware.CompareNetObjects;
using LibGit2Sharp;
using Microsoft.ApplicationInspector.RulesEngine;
using Microsoft.DevSkim.CLI.Options;
using Microsoft.Extensions.Logging;
using ShellProgressBar;

namespace Microsoft.DevSkim.CLI.Commands
{
    public class AnalyzeCommand
    {
        // Not readonly, this field can be modified by the configure method
        private BaseAnalyzeCommandOptions _opts;
        private readonly ILoggerFactory _logFactory;
        private readonly ILogger<AnalyzeCommand> _logger;

        /// <summary>
        /// Create an instance of the Analyze command with the specified options. To execute the command with the options given call <see cref="Run"/>
        /// </summary>
        /// <param name="options"></param>
        public AnalyzeCommand(AnalyzeCommandOptions options)
        {
            _opts = options;
            _logFactory = _opts.GetLoggerFactory();
            _logger = _logFactory.CreateLogger<AnalyzeCommand>();
        }

        /// <summary>
        /// Run analysis based on the options provided to the constructor.
        /// </summary>
        /// <returns>A negative or 0 int representing a <see cref="ExitCode"/>, when `ExitCodeIsNumIssues` is set, positive numbers indicate the number if issues identified by the scan</returns>
        public int Run()
        {
            (ExitCode exitCode, string? fullPath, Languages? languages) = Configure();

            if (exitCode != ExitCode.Okay)
            {
                return (int)exitCode;
            }

            if (string.IsNullOrEmpty(fullPath))
            {
                _logger.LogError("The path to source code was null or empty.");
                return (int)ExitCode.ArgumentParsingError;
            }

            if (languages is null)
            {
                _logger.LogError("Languages could not be instantiated.");
                return (int)ExitCode.CriticalError;
            }

            // Pass the path and let RunFileEntries handle file discovery with progress
            return RunFileEntries(fullPath, languages);
        }

        /// <summary>
        /// Configure the options and return the full path to scan if successful
        /// </summary>
        /// <returns></returns>
        private (ExitCode, string?, Languages?) Configure()
        {
            // Ensure that the target to scan exists
            if (!Directory.Exists(_opts.Path) && !File.Exists(_opts.Path))
            {
                _logger.LogError("Error: Not a valid file or directory {0}", _opts.Path);

                return (ExitCode.CriticalError, null, null);
            }

            if (_opts is AnalyzeCommandOptions optsWithJson)
            {
                // Check if the options json is specified.
                if (!string.IsNullOrEmpty(optsWithJson.PathToOptionsJson))
                {
                    if (File.Exists(optsWithJson.PathToOptionsJson))
                    {
                        try
                        {
                            var deserializedOptions =
                                JsonSerializer.Deserialize<SerializedAnalyzeCommandOptions>(File.ReadAllText(optsWithJson.PathToOptionsJson), new JsonSerializerOptions(){Converters = { new JsonStringEnumConverter() }});
                            if (deserializedOptions is { })
                            {
                                // For each property in the opts argument, if the argument is not default, override the equivalent from the deserialized options
                                var serializedProperties = typeof(SerializedAnalyzeCommandOptions).GetProperties();
                                foreach (var prop in typeof(BaseAnalyzeCommandOptions).GetProperties())
                                {
                                    var value = prop.GetValue(_opts);
                                    // Get the option attribute from the property
                                    var maybeOptionAttribute = prop.GetCustomAttributes(true).Where(x => x is OptionAttribute).FirstOrDefault();
                                    var compareLogic = new CompareLogic();
                                    if (maybeOptionAttribute is OptionAttribute optionAttribute)
                                    {
                                        // Check if the option attributes default value differs from the value in the CLI provided options
                                        //   If the CLI provided a non-default option, override the deserialized option
                                        // The values not default if the default is null and the set value is not
                                        if ((optionAttribute.Default is null && value is not null) 
                                            // Or the value is set to non-null and the values are not equal
                                            // Also handles enumerable comparisons
                                            || (!compareLogic.Compare(optionAttribute.Default, value).AreEqual))
                                        {
                                            var selectedProp =
                                                serializedProperties.FirstOrDefault(x => x.HasSameMetadataDefinitionAs(prop));
                                            selectedProp?.SetValue(deserializedOptions, value);
                                        }
                                    }
                                }

                                // Replace the regular options with the deserialized options
                                _opts = deserializedOptions;
                            }

                        }
                        catch (Exception e)
                        {
                            _logger.LogError("Error while parsing additional options {0}", e.Message);
                            return (ExitCode.CriticalError, null, null);
                        }
                    }
                }
            }


            string fp = Path.GetFullPath(_opts.Path);
            if (string.IsNullOrEmpty(fp))
            {
                _logger.LogError("Provided scan path was empty or null.");
                return (ExitCode.CriticalError, null, null);
            }
            if (string.IsNullOrEmpty(_opts.BasePath))
            {
                _opts.BasePath = fp;
            }
            if (!string.IsNullOrEmpty(_opts.OutputFile))
            {
                _opts.OutputFile = Path.Combine(Environment.CurrentDirectory, _opts.OutputFile);
            }

            Languages? languages = null;
            if (!string.IsNullOrEmpty(_opts.CommentsPath) || !string.IsNullOrEmpty(_opts.LanguagesPath))
            {
                if (string.IsNullOrEmpty(_opts.CommentsPath) || string.IsNullOrEmpty(_opts.LanguagesPath))
                {
                    _logger.LogError("When either comments or languages are specified both must be specified.");
                    return (ExitCode.ArgumentParsingError, null, null);

                }

                try
                {
                    languages = DevSkimLanguages.FromFiles(_opts.CommentsPath, _opts.LanguagesPath);
                }
                catch (Exception e)
                {
                    _logger.LogError($"Either the Comments or Languages file was not able to be read. ({e.Message})");
                    return (ExitCode.ArgumentParsingError, null, null);
                }
            }
            languages ??= DevSkimLanguages.LoadEmbedded();

            return (ExitCode.Okay, fp, languages);
        }

        /// <summary>
        /// Based on the options, return an enumeration of the files from the path.  For example, if crawl archives is set, will crawl into archives if possible, otherwise just returns the file itself in a FileEntry wrapper
        /// </summary>
        /// <param name="opts"></param>
        /// <param name="file"></param>
        /// <param name="extractor"></param>
        /// <param name="extractorOptions"></param>
        /// <returns></returns>
        private IEnumerable<FileEntry> FilePathToFileEntries(BaseAnalyzeCommandOptions opts, string file, Extractor extractor, ExtractorOptions extractorOptions)
        {
            if (opts.CrawlArchives)
            {
                return extractor.Extract(file, extractorOptions);
            }

            return extractorOptions.FileNamePasses(file) ? FilenameToFileEntryArray(file) : Array.Empty<FileEntry>();
        }

        /// <summary>
        /// Checks if the file path is ignored by git
        /// </summary>
        /// <param name="fp"></param>
        /// <returns></returns>
        private static bool IsGitIgnored(string fp)
        {
            Process? process = Process.Start(new ProcessStartInfo("git")
            {
                Arguments = $"check-ignore {fp}",
                WorkingDirectory = Directory.GetParent(fp)?.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true // Suppress git errors being printed - particularly when not in a git work tree
            });
            process?.WaitForExit();
            string? stdOut = process?.StandardOutput.ReadToEnd();
            return process?.ExitCode == 0 && stdOut?.Length > 0;
        }

        /// <summary>
        /// Checks if git is available on the path
        /// </summary>
        /// <returns></returns>
        private static bool IsGitPresent()
        {
            Process? process = Process.Start(new ProcessStartInfo("git")
            {
                Arguments = "--version"
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }

        string TryRelativizePath(string parentPath, string childPath)
        {
            try
            {
                if (_opts.AbsolutePaths)
                {
                    return Path.GetFullPath(childPath);
                }
                if (parentPath == childPath)
                {
                    if (File.Exists(parentPath))
                    {
                        return Path.GetFileName(childPath);
                    }
                }
                return Path.GetRelativePath(parentPath, childPath) ?? childPath;
            }
            catch (Exception)
            {
                // Paths weren't relative.
            }
            return childPath;
        }

        private int RunFileEntries(string scanPath, Languages devSkimLanguages)
        {
            // Progress bar options
            var progressBarOptions = new ProgressBarOptions
            {
                ForegroundColor = ConsoleColor.Yellow,
                ForegroundColorDone = ConsoleColor.DarkGreen,
                BackgroundColor = ConsoleColor.DarkGray,
                BackgroundCharacter = '\u2593',
                DisableBottomPercentage = false,
                ShowEstimatedDuration = true
            };

            // Step 1: Discover files with progress bar
            List<FileEntry> fileList;
            Extractor extractor = new Extractor();
            ExtractorOptions extractorOpts = new ExtractorOptions() 
            { 
                ExtractSelfOnFail = false, 
                AllowFilters = _opts.AllowGlobs, 
                DenyFilters = _opts.Globs 
            };

            if (!_opts.DisableProgress)
            {
                // For large repos, we don't know the total count ahead of time
                // So we'll use an indeterminate progress bar or estimated count
                using (var discoveryProgressBar = new ProgressBar(100, "Discovering files", progressBarOptions))
                {
                    fileList = new List<FileEntry>();
                    
                    // Analysing a single file
                    if (!Directory.Exists(scanPath))
                    {
                        discoveryProgressBar.Tick(50, "Checking file...");
                        
                        if (_opts.RespectGitIgnore)
                        {
                            if (IsGitPresent())
                            {
                                if (IsGitIgnored(scanPath))
                                {
                                    _logger.LogError("The file specified was ignored by gitignore.");
                                    return (int)ExitCode.CriticalError;
                                }

                                fileList.AddRange(FilePathToFileEntries(_opts, scanPath, extractor, extractorOpts));
                            }
                            else
                            {
                                _logger.LogError("Could not detect git on path. Unable to use gitignore.");
                                return (int)ExitCode.CriticalError;
                            }
                        }
                        else
                        {
                            fileList.AddRange(FilePathToFileEntries(_opts, scanPath, extractor, extractorOpts));
                        }
                        
                        discoveryProgressBar.Tick(100, $"Found {fileList.Count} file(s)");
                    }
                    // Analyzing a directory
                    else
                    {
                        discoveryProgressBar.Tick(10, "Scanning directory structure...");
                        
                        if (_opts.RespectGitIgnore)
                        {
                            if (IsGitPresent())
                            {
                                IEnumerable<string> files = Directory.EnumerateFiles(scanPath, "*.*", SearchOption.AllDirectories);
                                var fileArray = files.ToArray(); // Materialize to get count
                                int discoveredFiles = fileArray.Length;
                                int processedFiles = 0;
                                
                                discoveryProgressBar.Tick(30, $"Found {discoveredFiles} files, checking gitignore...");

                                foreach (string file in fileArray)
                                {
                                    if (!IsGitIgnored(file))
                                    {
                                        fileList.AddRange(FilePathToFileEntries(_opts, file, extractor, extractorOpts));
                                    }
                                    
                                    processedFiles++;
                                    if (processedFiles % 100 == 0 || processedFiles == discoveredFiles)
                                    {
                                        int progress = 30 + (int)((double)processedFiles / discoveredFiles * 70);
                                        discoveryProgressBar.Tick(progress, 
                                            $"Processed {processedFiles}/{discoveredFiles} files, found {fileList.Count} to analyze");
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogError("Could not detect git on path. Unable to use gitignore.");
                                return (int)ExitCode.CriticalError;
                            }
                        }
                        else
                        {
                            IEnumerable<string> files = Directory.EnumerateFiles(scanPath, "*.*", SearchOption.AllDirectories);
                            var fileArray = files.ToArray();
                            int discoveredFiles = fileArray.Length;
                            int processedFiles = 0;
                            
                            discoveryProgressBar.Tick(30, $"Found {discoveredFiles} files...");

                            foreach (string file in fileArray)
                            {
                                fileList.AddRange(FilePathToFileEntries(_opts, file, extractor, extractorOpts));
                                
                                processedFiles++;
                                if (processedFiles % 100 == 0 || processedFiles == discoveredFiles)
                                {
                                    int progress = 30 + (int)((double)processedFiles / discoveredFiles * 70);
                                    discoveryProgressBar.Tick(progress, 
                                        $"Processed {processedFiles}/{discoveredFiles} files");
                                }
                            }
                        }
                        
                        discoveryProgressBar.Tick(100, $"Discovered {fileList.Count} files to analyze");
                    }
                }
            }
            else
            {
                // No progress bar - original logic
                fileList = new List<FileEntry>();
                
                if (!Directory.Exists(scanPath))
                {
                    if (_opts.RespectGitIgnore)
                    {
                        if (IsGitPresent())
                        {
                            if (IsGitIgnored(scanPath))
                            {
                                _logger.LogError("The file specified was ignored by gitignore.");
                                return (int)ExitCode.CriticalError;
                            }

                            fileList.AddRange(FilePathToFileEntries(_opts, scanPath, extractor, extractorOpts));
                        }
                        else
                        {
                            _logger.LogError("Could not detect git on path. Unable to use gitignore.");
                            return (int)ExitCode.CriticalError;
                        }
                    }
                    else
                    {
                        fileList.AddRange(FilePathToFileEntries(_opts, scanPath, extractor, extractorOpts));
                    }
                }
                else
                {
                    if (_opts.RespectGitIgnore)
                    {
                        if (IsGitPresent())
                        {
                            IEnumerable<string> files = Directory.EnumerateFiles(scanPath, "*.*", SearchOption.AllDirectories)
                                .Where(fileName => !IsGitIgnored(fileName));
                            foreach (string? notIgnoredFileName in files)
                            {
                                fileList.AddRange(FilePathToFileEntries(_opts, notIgnoredFileName, extractor, extractorOpts));
                            }
                        }
                        else
                        {
                            _logger.LogError("Could not detect git on path. Unable to use gitignore.");
                            return (int)ExitCode.CriticalError;
                        }
                    }
                    else
                    {
                        IEnumerable<string> files = Directory.EnumerateFiles(scanPath, "*.*", SearchOption.AllDirectories);
                        foreach (string file in files)
                        {
                            fileList.AddRange(FilePathToFileEntries(_opts, file, extractor, extractorOpts));
                        }
                    }
                }
            }

            var totalFiles = fileList.Count;

            // Step 2: Load and verify rules
            DevSkimRuleSet devSkimRuleSet;

            if (!_opts.DisableProgress)
            {
                using (var ruleLoadingProgressBar = new ProgressBar(100, "Loading rules", progressBarOptions))
                {
                    // Load default or empty ruleset
                    ruleLoadingProgressBar.Tick(10, "Loading default rules...");
                    devSkimRuleSet = _opts.IgnoreDefaultRules ? new() : DevSkimRuleSet.GetDefaultRuleSet();
                    int defaultRulesCount = devSkimRuleSet.Count();
                    ruleLoadingProgressBar.Tick(30, $"Loaded {defaultRulesCount} default rules");
                    
                    // Load custom rules if provided
                    if (_opts.Rules.Any())
                    {
                        int rulesProcessed = 0;
                        int totalRulePaths = _opts.Rules.Count();
                        
                        foreach (string path in _opts.Rules)
                        {
                            devSkimRuleSet.AddPath(path);
                            rulesProcessed++;
                            int currentRulesCount = devSkimRuleSet.Count();
                            int progress = 30 + (int)((double)rulesProcessed / totalRulePaths * 40);
                            ruleLoadingProgressBar.Tick(progress, $"Loaded {rulesProcessed}/{totalRulePaths} custom rule paths ({currentRulesCount} total rules)");
                        }
                        ruleLoadingProgressBar.Tick(70, $"Loaded {devSkimRuleSet.Count()} total rules");
                    }
                    else
                    {
                        ruleLoadingProgressBar.Tick(70, $"Using {defaultRulesCount} rules");
                    }
                    
                    // Apply rule ID filters
                    if (_opts.RuleIds.Any())
                    {
                        ruleLoadingProgressBar.Tick(75, $"Filtering to {_opts.RuleIds.Count()} specific rule IDs...");
                        devSkimRuleSet = devSkimRuleSet.WithIds(_opts.RuleIds);
                    }

                    if (_opts.IgnoreRuleIds.Any())
                    {
                        ruleLoadingProgressBar.Tick(77, $"Excluding {_opts.IgnoreRuleIds.Count()} rule IDs...");
                        devSkimRuleSet = devSkimRuleSet.WithoutIds(_opts.IgnoreRuleIds);
                    }
                    
                    // Verify rules
                    ruleLoadingProgressBar.Tick(80, $"Verifying {devSkimRuleSet.Count()} rules...");
                    DevSkimRuleVerifier devSkimVerifier = new DevSkimRuleVerifier(new DevSkimRuleVerifierOptions()
                    {
                        LanguageSpecs = devSkimLanguages,
                        LoggerFactory = _logFactory
                    });

                    DevSkimRulesVerificationResult result = devSkimVerifier.Verify(devSkimRuleSet);

                    if (!result.Verified)
                    {
                        _logger.LogError("Error: Rules failed validation. ");
                        return (int)ExitCode.CriticalError;
                    }

                    ruleLoadingProgressBar.Tick(100, $"Loaded and verified {devSkimRuleSet.Count()} rules");
                }
            }
            else
            {
                // No progress bar
                devSkimRuleSet = _opts.IgnoreDefaultRules ? new() : DevSkimRuleSet.GetDefaultRuleSet();
        
                if (_opts.Rules.Any())
                {
                    foreach (string path in _opts.Rules)
                    {
                        devSkimRuleSet.AddPath(path);
                    }
                }

                // Apply rule ID filters
                if (_opts.RuleIds.Any())
                {
                    devSkimRuleSet = devSkimRuleSet.WithIds(_opts.RuleIds);
                }

                if (_opts.IgnoreRuleIds.Any())
                {
                    devSkimRuleSet = devSkimRuleSet.WithoutIds(_opts.IgnoreRuleIds);
                }

                DevSkimRuleVerifier devSkimVerifier = new DevSkimRuleVerifier(new DevSkimRuleVerifierOptions()
                {
                    LanguageSpecs = devSkimLanguages,
                    LoggerFactory = _logFactory
                });

                DevSkimRulesVerificationResult result = devSkimVerifier.Verify(devSkimRuleSet);

                if (!result.Verified)
                {
                    _logger.LogError("Error: Rules failed validation. ");
                    return (int)ExitCode.CriticalError;
                }
            }

            if (!devSkimRuleSet.Any())
            {
                _logger.LogError("Error: No rules were loaded. ");
                return (int)ExitCode.CriticalError;
            }

            Severity severityFilter = Severity.Unspecified;
            foreach (Severity severity in _opts.Severities)
            {
                severityFilter |= severity;
            }

            Confidence confidenceFilter = Confidence.Unspecified;
            foreach (Confidence confidence in _opts.Confidences)
            {
                confidenceFilter |= confidence;
            }

            // Initialize the processor
            DevSkimRuleProcessorOptions devSkimRuleProcessorOptions = new DevSkimRuleProcessorOptions()
            {
                Languages = devSkimLanguages,
                AllowAllTagsInBuildFiles = true,
                LoggerFactory = _logFactory,
                Parallel = !_opts.DisableParallel,
                SeverityFilter = severityFilter,
                ConfidenceFilter = confidenceFilter,
                EnableSuppressions = !_opts.DisableSuppression
            };

            DevSkimRuleProcessor processor = new DevSkimRuleProcessor(devSkimRuleSet, devSkimRuleProcessorOptions);
            GitInformation? information = GenerateGitInformation(Path.GetFullPath(_opts.Path));
            Writer outputWriter = WriterFactory.GetWriter(string.IsNullOrEmpty(_opts.OutputFileFormat) ? "text" : _opts.OutputFileFormat,
                                                           _opts.OutputTextFormat,
                                                           string.IsNullOrEmpty(_opts.OutputFile) ? Console.Out : File.CreateText(_opts.OutputFile),
                                                           _opts.OutputFile,
                                                           information);

            int filesAnalyzed = 0;
            int filesSkipped = 0;
            int filesAffected = 0;
            int issuesCount = 0;

            // Step 3: Analyze files
            void parseFileEntry(FileEntry fileEntry, IProgressBar? progressBar)
            {
                devSkimLanguages.FromFileNameOut(fileEntry.Name, out LanguageInfo languageInfo);

                // Skip files written in unknown language
                if (string.IsNullOrEmpty(languageInfo.Name))
                {
                    Interlocked.Increment(ref filesSkipped);
                }
                else
                {
                    string fileText = string.Empty;

                    try
                    {
                        using (StreamReader reader = new StreamReader(fileEntry.Content))
                        {
                            fileText = reader.ReadToEnd();
                        }
                        Interlocked.Increment(ref filesAnalyzed);
                    }
                    catch (Exception)
                    {
                        // Skip files we can't parse
                        Interlocked.Increment(ref filesSkipped);
                        progressBar?.Tick($"Analyzed {filesAnalyzed + filesSkipped}/{totalFiles} files - Found {issuesCount} issues");
                        return;
                    }

                    List<Issue> issues = processor.Analyze(fileText, fileEntry.Name).ToList();
                    if (_opts is SerializedAnalyzeCommandOptions serializedAnalyzeCommandOptions)
                    {
                        if (serializedAnalyzeCommandOptions.LanguageRuleIgnoreMap.TryGetValue(languageInfo.Name,
                                out List<string>? maybeRulesToIgnore) && maybeRulesToIgnore is { } rulesToIgnore)
                        {
                            var numRemoved = issues.RemoveAll(x => rulesToIgnore.Contains(x.Rule.Id));
                            _logger.LogDebug($"Removed {numRemoved} results because of language rule filters.");
                        }
                    }
                    
                    issues.Sort((issue1, issue2) => issue1.Boundary.Index - issue2.Boundary.Index);

                    bool issuesFound = issues.Any(iss => !iss.IsSuppressionInfo) || _opts.DisableSuppression;

                    if (issuesFound)
                    {
                        Interlocked.Increment(ref filesAffected);
                        _logger.LogDebug("file:{0}", fileEntry.FullPath);

                        foreach (Issue issue in issues)
                        {
                            if (!issue.IsSuppressionInfo || _opts.DisableSuppression)
                            {
                                Interlocked.Increment(ref issuesCount);
                                _logger.LogDebug("\tregion:{0},{1},{2},{3} - {4} [{5}] - {6}",
                                                        issue.StartLocation.Line,
                                                        issue.StartLocation.Column,
                                                        issue.EndLocation.Line,
                                                        issue.EndLocation.Column,
                                                        issue.Rule.Id,
                                                        issue.Rule.Severity,
                                                        issue.Rule.Name);
                                var issueText = fileText.Substring(issue.Boundary.Index, issue.Boundary.Length);
                                IssueRecord record = new DevSkim.IssueRecord(
                                    Filename: TryRelativizePath(_opts.BasePath, fileEntry.FullPath),
                                    Filesize: fileText.Length,
                                    TextSample: _opts.SkipExcerpts ? string.Empty : issueText,
                                    Issue: issue,
                                    Language: languageInfo.Name,
                                    Fixes: issue.Rule.Fixes?.Where(x => DevSkimRuleProcessor.IsFixable(issueText, x)).ToList());

                                outputWriter.WriteIssue(record);
                            }
                        }
                    }
                }
                
                // Update progress bar
                progressBar?.Tick($"Analyzed {filesAnalyzed + filesSkipped}/{totalFiles} files - Found {issuesCount} issues");
            }

            // Analyze files with progress bar
            if (!_opts.DisableProgress && totalFiles > 0)
            {
                using (var fileAnalysisProgressBar = new ProgressBar(totalFiles, "Analyzing files", progressBarOptions))
                {
                    if (_opts.DisableParallel)
                    {
                        foreach (FileEntry fileEntry in fileList)
                        {
                            parseFileEntry(fileEntry, fileAnalysisProgressBar);
                        }
                    }
                    else
                    {
                        Parallel.ForEach(fileList, fileEntry => parseFileEntry(fileEntry, fileAnalysisProgressBar));
                    }
                }
            }
            else
            {
                // No progress bar
                if (_opts.DisableParallel)
                {
                    foreach (FileEntry fileEntry in fileList)
                    {
                        parseFileEntry(fileEntry, null);
                    }
                }
                else
                {
                    Parallel.ForEach(fileList, fileEntry => parseFileEntry(fileEntry, null));
                }
            }

            outputWriter.FlushAndClose();

            _logger.LogDebug("Issues found: {0} in {1} files", issuesCount, filesAffected);
            _logger.LogDebug("Files analyzed: {0}", filesAnalyzed);
            _logger.LogDebug("Files skipped: {0}", filesSkipped);

            return _opts.ExitCodeIsNumIssues ? (issuesCount > 0 ? issuesCount : (int)ExitCode.NoIssues) : (int)ExitCode.NoIssues;
        }

        private GitInformation? GenerateGitInformation(string optsPath)
        {
            try
            {
                using Repository repo = new Repository(optsPath);
                GitInformation info = new GitInformation()
                {
                    Branch = repo.Head.FriendlyName
                };
                if (repo.Network.Remotes.Any())
                {
                    info.RepositoryUri = new Uri(repo.Network.Remotes.First().Url);
                }
                if (repo.Head.Commits.Any())
                {
                    info.CommitHash = repo.Head.Commits.First().Sha;
                }

                return info;
            }
            catch
            {
                if (Directory.GetParent(optsPath) is { } notNullParent)
                {
                    return GenerateGitInformation(notNullParent.FullName);
                }
            }

            return null;
        }

        /// <summary>
        /// Open a read stream for the given file name and return a collection with a single file entry representing that file, or an empty collection if the file could not be read
        /// </summary>
        /// <param name="pathToFile"></param>
        /// <returns></returns>
        private ICollection<FileEntry> FilenameToFileEntryArray(String pathToFile)
        {
            try
            {
                FileStream fs = new FileStream(pathToFile, FileMode.Open, FileAccess.Read);
                return new FileEntry[] { new FileEntry(pathToFile, fs, null, true) };
            }
            catch (Exception e)
            {
                _logger.LogDebug("The file located at {0} could not be read. ({1}:{2})", pathToFile, e.GetType().Name, e.Message);
            }
            return Array.Empty<FileEntry>();
        }
    }
}