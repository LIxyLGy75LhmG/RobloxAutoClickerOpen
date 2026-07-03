using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using AutoClicker.Models;
using Serilog;

namespace AutoClicker.Utils
{
    public static class KeyMappingUtils
    {
        public static List<KeyMapping> KeyMapping { get; set; }

        static KeyMappingUtils()
        {
            LoadMapping();
        }

        private static void LoadMapping()
        {
            if (KeyMapping != null)
                return;
            ReadMapping();
        }

        public static KeyMapping GetKeyMappingByCode(int virtualKeyCode)
        {
            return KeyMapping?.Find(keyMapping => keyMapping.VirtualKeyCode == virtualKeyCode);
        }

        // keyMappings.json is embedded in the assembly (single-file exe) — read it from the manifest,
        // never from disk. Always leave KeyMapping as a non-null list so callers can't NRE on startup.
        private static void ReadMapping()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(name => name.EndsWith(Constants.KEY_MAPPINGS_RESOURCE_PATH, StringComparison.OrdinalIgnoreCase));

                if (resourceName == null)
                {
                    Log.Warning("Embedded resource {Resource} not found", Constants.KEY_MAPPINGS_RESOURCE_PATH);
                    KeyMapping = new List<KeyMapping>();
                    return;
                }

                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                using (StreamReader reader = new StreamReader(stream))
                {
                    string json = reader.ReadToEnd();
                    KeyMapping = JsonSerializer.Deserialize<List<KeyMapping>>(json) ?? new List<KeyMapping>();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed loading embedded key mappings");
                KeyMapping = new List<KeyMapping>();
            }
        }
    }
}
