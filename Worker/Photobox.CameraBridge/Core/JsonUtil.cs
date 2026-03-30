// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 Andreas Rottmann
//
// Datei: JsonUtil.cs
// Zweck: Enthält Hilfsfunktionen für JSON-Dateien und JSON-Streams.
// Projekt: Photobox CameraBridge Worker
//
// Aufgaben:
// - DataContract-Objekte aus Dateien laden
// - Objekte als JSON in Streams schreiben
// - Textinhalte aus Streams einlesen

using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Photobox.CameraBridge.Core
{
    internal static class JsonUtil
    {
        public static T LoadFile<T>(string path)
        {
            using (var fs = File.OpenRead(path))
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                return (T)ser.ReadObject(fs);
            }
        }

        public static void WriteJson<T>(Stream output, T obj)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            ser.WriteObject(output, obj);
        }

        public static string ReadAllText(Stream input)
        {
            using (var sr = new StreamReader(input, Encoding.UTF8))
                return sr.ReadToEnd();
        }
    }
}