﻿using Loxifi;
using Penguin.Configuration.Abstractions.Interfaces;
using Penguin.Configuration.Providers;
using Penguin.DependencyInjection;
using Penguin.DependencyInjection.ServiceProviders;
using Penguin.DependencyInjection.ServiceScopes;
using Penguin.Messaging.Core;
using Penguin.Messaging.Logging.Extensions;
using Penguin.Messaging.Logging.Messages;
using Penguin.Reflection;
using Penguin.Workers.Abstractions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Penguin.Workers.Harness
{
    /// <summary>
    /// A base class for providing common implementations of IWorker functionality
    /// </summary>
    public class WorkerHarness
    {
        /// <summary>
        /// The Directory the log information should be stored in
        /// </summary>
        public virtual DirectoryInfo LogDirectory { get; set; }

        /// <summary>
        /// The message bus used to push worker messages
        /// </summary>
        public virtual MessageBus MessageBus { get; set; }

        /// <summary>
        /// The root directory to be treated as the application execution directory
        /// </summary>
        public virtual DirectoryInfo RunFolder
        {
            get
            {
                DirectoryInfo thisDir = new(Path.Combine(Directory.GetCurrentDirectory()));

                if (!thisDir.Exists)
                {
                    thisDir.Create();
                }

                return thisDir;
            }
        }

        /// <summary>
        /// The folder to generate scripts to for launching workers
        /// </summary>
        public virtual DirectoryInfo ScriptsFolder
        {
            get
            {
                DirectoryInfo thisDir = new(Path.Combine(Directory.GetCurrentDirectory(), "Scripts"));

                if (!thisDir.Exists)
                {
                    thisDir.Create();
                }

                return thisDir;
            }
        }

        /// <summary>
        /// The time the worker harness was created
        /// </summary>
        public virtual DateTime Start => DateTime.Now;

        internal static bool Console_present
        {
            get
            {
                if (_console_present == null)
                {
                    _console_present = true;
                    try
                    { int window_height = Console.WindowHeight; }
                    catch { _console_present = false; }
                }
                return _console_present.Value;
            }
        }

        /// <summary>
        /// The configuration providers to be passed to worker instances
        /// </summary>
        protected IProvideConfigurations[] Configs { get; set; }

        private static readonly object logLock = new();

        private static bool? _console_present;

        /// <summary>
        /// Constructs a new instance of the worker harness
        /// </summary>
        /// <param name="configs">A list of configuration providers to pass to the worker instances</param>
        public WorkerHarness(params IProvideConfigurations[] configs)
        {
            Configs = configs;
        }

        /// <summary>
        /// The .net configuration provider doesn't play nice with the old configs. This attempts to clean it up just enough to get the information we need from it
        /// https://github.com/aspnet/Extensions/pull/862
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public static void SanitizeConfig(string path)
        {
            string Cleaned = "<!--cleaned-->";

            List<string> oldLines = File.ReadAllLines(path).ToList();
            List<string> newLines = new();

            if (oldLines.Last() == Cleaned)
            {
                return;
            }

            int SkipDepth = 0;

            List<(string StartTag, string EndTag)> SkipSections = new()
            {
                //("<runtime>","</runtime>"),
                ("<controls>","</controls>"),
                ("<httpModules>","</httpModules>"),
                ("<system.web>", "</system.web>"),
                ("<system.net>", "</system.net>"),
                ("<system.webServer>", "</system.webServer>"),
                ("<system.codedom>", "</system.codedom>")
            };

            List<string> StripTags = new()
            {
                "<location",
                "</location>",
                "<system.webServer />"
            };

            Dictionary<string, List<string>> InsertSections = new()
            {
                ["<configSections>"] = new List<string>()
                {
                      "<section name=\"appSettings\" type=\"System.Configuration.AppSettingsSection, System.Configuration, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a\" restartOnExternalChanges=\"false\" requirePermission=\"false\"/>",
                      "<section name=\"connectionStrings\" type=\"System.Configuration.ConnectionStringsSection, System.Configuration, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a\" requirePermission=\"false\"/>"
                }
            };

            List<string> TagsToName = new()
            {
                "add",
                "remove",
                "location"
            };

            foreach (string s in oldLines)
            {
                if (SkipSections.Any(ss => s.Contains(ss.StartTag)))
                {
                    SkipDepth++;
                }

                if (SkipDepth == 0 && !StripTags.Any(s.Contains))
                {
                    {
                        newLines.Add(s);
                    }
                }

                if (InsertSections.Any(kvp => s.Contains(kvp.Key)))
                {
                    foreach (string toInsert in InsertSections[InsertSections.First(kvp => s.Contains(kvp.Key)).Key])
                    {
                        newLines.Add(toInsert);
                    }
                }

                if (SkipSections.Any(ss => s.Contains(ss.EndTag)))
                {
                    SkipDepth--;
                }
            }

            newLines.Add(Cleaned);

            File.WriteAllLines(path, newLines.Where(n => !string.IsNullOrWhiteSpace(n)));
        }

        /// <summary>
        /// Generates scripts for launching individual workers to the build directory in the scripts folder
        /// </summary>
        public void GenerateScripts()
        {
            foreach (Type t in TypeFactory.Default.GetAllImplementations(typeof(IWorker)))
            {
                List<string> scriptLines = new() {
                "cd ..",
                $"IF EXIST {Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location)}.exe (",
                $"{Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location)}.exe " + t.FullName,
                $") ELSE (",
                $"dotnet {Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly().Location)}.dll " + t.FullName,
                ")"
                };

                List<string> manualScriptLines = new() {
                "@echo off",
                "",
                ":: BatchGotAdmin",
                ":-------------------------------------",
                "REM  --> Check for permissions",
                "    IF \"%PROCESSOR_ARCHITECTURE%\" EQU \"amd64\" (",
                ">nul 2>&1 \"%SYSTEMROOT%\\SysWOW64\\cacls.exe\" \"%SYSTEMROOT%\\SysWOW64\\config\\system\"",
                ") ELSE (",
                ">nul 2>&1 \"%SYSTEMROOT%\\system32\\cacls.exe\" \"%SYSTEMROOT%\\system32\\config\\system\"",
                ")",
                "",
                "REM --> If error flag set, we do not have admin.",
                "if '%errorlevel%' NEQ '0' (",
                "    echo Requesting administrative privileges...",
                "    goto UACPrompt",
                ") else ( goto gotAdmin )",
                "",
                ":UACPrompt",
                "    echo Set UAC = CreateObject^(\"Shell.Application\"^) > \"%temp%\\getadmin.vbs\"",
                "    set params= %*",
                "    echo UAC.ShellExecute \"cmd.exe\", \"/c \"\"%~s0\"\" %params:\"=\"\"%\", \"\", \"runas\", 1 >> \"%temp%\\getadmin.vbs\"",
                "",
                "    \"%temp%\\getadmin.vbs\"",
                "    del \"%temp%\\getadmin.vbs\"",
                "    exit /B",
                "",
                ":gotAdmin",
                "    pushd \"%CD%\"",
                "    CD /D \"%~dp0\"",
                ":-------------------------------------- "
                };

                manualScriptLines.AddRange(scriptLines);

                manualScriptLines.Add("pause");

                File.WriteAllLines(Path.Combine(ScriptsFolder.FullName, t.Name + ".bat"), scriptLines);
                File.WriteAllLines(Path.Combine(ScriptsFolder.FullName, t.Name + ".Manual.bat"), manualScriptLines);
            }
        }

        /// <summary>
        /// Kicks off a worker by the type name
        /// </summary>
        /// <param name="TypeFullName">The name of the worker to kick off</param>
        /// <param name="TypeMapping">An optional TypeName/Type dictionary used for mapping and optionally ensuring that the referenced DLL's deploy with the application</param>
        /// <param name="args"></param>
        /// <returns>A result code from the worker</returns>
        [Obsolete]
        public int RunWorker(string TypeFullName, Dictionary<string, Type> TypeMapping = null, params string[] args)
        {
            using ScopedServiceScope serviceScope = new();
            MessageBus = new MessageBus(serviceScope.ServiceProvider);

            if (!Engine.IsRegistered<IServiceProvider>())
            {
                Engine.RegisterInstance<IServiceProvider>(serviceScope.ServiceProvider, typeof(ScopedServiceProvider));
            }

            Engine.RegisterInstance<IProvideConfigurations>(new ConfigurationProviderList(Configs));

            Setup();

            return Execute(TypeFullName, serviceScope.ServiceProvider, TypeMapping, args);
        }

        internal void LogError(LogMessage log)
        {
            string toLog = $"{log.Level}: {log.Message}";

            if (Console_present)
            {
                Console.WriteLine(toLog);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(toLog);
            }

            lock (logLock)
            {
                File.AppendAllText(Path.Combine(LogDirectory.FullName, $"Log_{Start:yyyyMMdd_HHmmss}.txt"), toLog + System.Environment.NewLine);
            }
        }

        private int Execute(string TypeFullName, IServiceProvider serviceProvider, Dictionary<string, Type> TypeMapping = null, params string[] args)
        {
            try
            {
                Type toInstantiate = TypeMapping != null && TypeMapping.TryGetValue(TypeFullName, out Type value) ? value : TypeFactory.Default.GetTypeByFullName(TypeFullName);
                if (toInstantiate is null)
                {
                    throw new ArgumentNullException($"Could not find type {TypeFullName} in optional mapping dictionary or using reflection over local dll's");
                }

                if (!toInstantiate.IsAbstract)
                {
                    Engine.Register(toInstantiate, toInstantiate, typeof(TransientServiceProvider));

                    IWorker thisWorker = serviceProvider.GetService(toInstantiate) as IWorker;

                    thisWorker.UpdateSync(true, args);
                }
            }
            catch (Exception ex)
            {
                MessageBus.Log(ex);

                return 1;
            }
            finally
            {
            }
            return 0;
        }

        private void Setup()
        {
            MessageBus.Subscribe((LogMessage log) => { LogError(log); });

            LogDirectory = new DirectoryInfo(Path.Combine(Directory.GetCurrentDirectory(), "Logs"));

            if (!LogDirectory.Exists)
            {
                LogDirectory.Create();
            }
        }
    }
}