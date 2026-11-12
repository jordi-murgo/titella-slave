// Process entry point of the compiled Windows payload. The bootstrap loader
// compiles the concatenated source to agent.exe with csc.exe
// (/main:Titella.Slave.AgentHost), so the agent no longer runs hosted inside a
// powershell.exe child: no classic PowerShell log channel records the
// bootstrap's own script text. With no arguments it reads agent.json (written
// by the loader next to the executable) and runs the agent; with --job it runs
// one piped job exactly like the former LoadFrom child did.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;

    internal static class AgentHost
    {
        private const string JobArgument = "--job";
        private const string ConfigName = "agent.json";

        public static int Main(string[] args)
        {
            if (args != null && args.Length == 1 && args[0] == JobArgument)
            {
                return JobEntry.Run();
            }
            if (args != null && args.Length == 0)
            {
                return RunAgent();
            }
            Console.Error.WriteLine(
                "titella agent: usage: agent.exe (agent) | agent.exe --job (one job)");
            return 2;
        }

        private static int RunAgent()
        {
            string configPath;
            try
            {
                string directory = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(directory))
                {
                    Console.Error.WriteLine("titella agent: the executable has no location");
                    return 2;
                }
                configPath = Path.Combine(directory, ConfigName);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("titella agent: " + error.Message);
                return 2;
            }
            string text;
            try
            {
                text = File.ReadAllText(configPath);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(
                    "titella agent: agent.json is unreadable: " + error.Message);
                return 2;
            }
            // agent.json carries exactly the arguments the former loader child
            // passed to Agent.Run: {"bootstrap_dir":..., "link_id":...,
            // "port":..., "heartbeat_ms":5000, "grace_ms":30000}. A config
            // missing any field is invalid, never defaulted.
            Dictionary<string, object> config;
            string bootstrapDirectory;
            string linkId;
            string port;
            long heartbeat;
            long grace;
            try
            {
                config = Json.Parse(text) as Dictionary<string, object>;
                if (config == null)
                {
                    throw new WireError("invalid_json", "agent.json is not an object");
                }
                bootstrapDirectory = GetText(config, "bootstrap_dir");
                linkId = GetText(config, "link_id");
                port = GetText(config, "port");
                if (!Json.TryGetInt64(GetOrNull(config, "heartbeat_ms"), out heartbeat) ||
                    !Json.TryGetInt64(GetOrNull(config, "grace_ms"), out grace) ||
                    heartbeat < 0 || heartbeat > int.MaxValue ||
                    grace < 0 || grace > int.MaxValue)
                {
                    throw new WireError(
                        "invalid_json",
                        "agent.json requires heartbeat_ms and grace_ms integers");
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("titella agent: agent.json is invalid: " + error.Message);
                return 2;
            }
            return Agent.Run(bootstrapDirectory, linkId, port,
                             (int)heartbeat, (int)grace);
        }

        private static object GetOrNull(Dictionary<string, object> config, string key)
        {
            object value;
            return config.TryGetValue(key, out value) ? value : null;
        }

        private static string GetText(Dictionary<string, object> config, string key)
        {
            string value = GetOrNull(config, key) as string;
            if (value == null)
            {
                throw new WireError("invalid_json", "agent.json requires " + key);
            }
            return value;
        }
    }
}
