﻿using System.IO;
using Newtonsoft.Json;
using System.Linq;
using System.Collections.Generic;
using System;
using System.Text.RegularExpressions;
using System.Data.SQLite;

namespace LibraryScrobbler.Lib
{
    public static class LibraryParsing
    {
        private static JsonSerializer _serializer = JsonSerializer.CreateDefault();

        public static void CreateDatabase(string sqliteFilepath)
        {
            if (File.Exists(sqliteFilepath))
                File.Delete(sqliteFilepath);

            SQLiteConnection.CreateFile(sqliteFilepath);
            string connectionString = $"DataSource={sqliteFilepath};";

            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    using (var command = new SQLiteCommand(_createSql, connection, transaction))
                    {
                        command.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                connection.Close();
            }
        }

private static string _createSql =
@"
CREATE TABLE MusicFile (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    Filename TEXT NOT NULL,
    ParsedOn INTEGER NOT NULL
);

CREATE TABLE Tag (
    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
    MusicFileId INTEGER NOT NULL,
    TagName TEXT NOT NULL,
    TagValue TEXT NOT NULL,
    ParsedOn INTEGER NOT NULL
);
";

        public static void ParseMetadata(
            DirectoryInfo inputDirectory,
            DirectoryInfo jsonOutputDirectory,
            string sqliteFilepath,
            bool shouldOverwrite)
        {
            var files = inputDirectory.EnumerateFiles();
            var validFiles = files.Where(n => SupportedExtensions.Contains(n.Extension));
            var fileTagMap = new Dictionary<string, ILookup<string, string>>();

            foreach (var file in validFiles)
            {
                var path = file.FullName;
                TagLib.File tagFile = null;
                try
                {
                    tagFile = TagLib.File.Create(path);
                }
                catch (Exception)
                {
                    // TODO: Add Logging
                    continue;
                }

                if (tagFile.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                {
                    // xiph is already structured like IEnumerable<IGrouping<string, string>>, but it does not implement ILookup or IGrouping.
                    // Because I couldn't find a clean way to form an ILookup from the contents of xiph, 
                    //   I flatten the fields & their contents into a list of tuples, and form the lookup from there.
                    var fieldMap = xiph.SelectMany(field =>
                        xiph.GetField(field).Select(value =>
                            new Tuple<string, string>(field, value)));
                    ILookup<string, string> tagMap = fieldMap.ToLookup(
                        n => n.Item1,
                        n => n.Item2);

                    fileTagMap.Add(file.Name, tagMap);
                    continue;
                }

                if (tagFile.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3File)
                {
                    ILookup<string, string> tagMap = GetID3v2TagMap(id3File);
                    fileTagMap.Add(file.Name, tagMap);
                    continue;
                }
            }

            if (fileTagMap.Any())
            {
                ExportJson(fileTagMap, jsonOutputDirectory, shouldOverwrite);
                ExportSqlite(fileTagMap, sqliteFilepath);
            }
        }

        private static int ExportSqlite(
            IReadOnlyDictionary<string, ILookup<string, string>> fileTagDictionary,
            string sqliteFilepath)
        {
            int numRowsAffected = 0;

            string connectionString = $"DataSource={sqliteFilepath};";
            using (var connection = new SQLiteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    foreach (var fileMap in fileTagDictionary)
                    {
                        using (var command = new SQLiteCommand(_insertMusicFileSql, connection, transaction))
                        {
                            command.Parameters.AddWithValue("filename", fileMap.Key);
                            numRowsAffected += command.ExecuteNonQuery();
                        }

                        long musicFileRowId = connection.LastInsertRowId;
                        foreach (IGrouping<string, string> tagMap in fileMap.Value)
                        {
                            string tagName = tagMap.Key;
                            foreach (string value in tagMap)
                            {
                                using (var command = new SQLiteCommand(_insertTagSql, connection, transaction))
                                {
                                    command.Parameters.AddWithValue("musicFileId", musicFileRowId);
                                    command.Parameters.AddWithValue("tagName", tagName);
                                    command.Parameters.AddWithValue("tagValue", value);
                                    numRowsAffected += command.ExecuteNonQuery();
                                }
                            }
                        }
                    }

                    transaction.Commit();
                }

                connection.Close();
            }

            return numRowsAffected;
        }

        private static string _insertMusicFileSql =
@"
INSERT INTO MusicFile (Filename, ParsedOn) 
VALUES (@filename, strftime('%s', 'now');
";

        private static string _insertTagSql =
@"
INSERT INTO Tag (MusicFileId, TagName, TagValue, ParsedOn)
VALUES (@musicFileId, @tagName, @tagValue, strftime('%s', 'now'));
";

        private static void ExportJson(
            IReadOnlyDictionary<string, ILookup<string, string>> fileTagMap,
            DirectoryInfo outputDirectory,
            bool shouldOverwrite)
        {
            var json = JsonConvert.SerializeObject(fileTagMap);
            var tagOutputPath = $"{outputDirectory.FullName}\\Json\\{outputDirectory.Name}.json";

            if (shouldOverwrite || !File.Exists(tagOutputPath))
            {
                Directory.CreateDirectory(outputDirectory.FullName);

                var writer = new StreamWriter(tagOutputPath);
                writer.AutoFlush = true;

                writer.Write(json);
            }
        }

        private static ILookup<string, string> GetID3v2TagMap(TagLib.Id3v2.Tag id3File)
        {
            // Id3 tags are guaranteed to be unique, but multiple tags might be mapped to the same field title
            //   Thus, there is no unique key to use for a dictionary.
            var tagValueMap = new List<Tuple<string, string>>();

            foreach (var frame in id3File)
            {
                string frameId = frame.FrameId.ToString();
                if (IgnoredId3Tags.Contains(frameId)) // Comments are often duplicated, just ignore them
                    continue;

                string title = GetId3v2TagTitle(frame);
                string value = GetID3v2TagValue(frame);

                tagValueMap.Add(new Tuple<string, string>(title, value));
            }

            return tagValueMap.ToLookup(n => n.Item1, n => n.Item2);
        }

        private static string GetId3v2TagTitle(TagLib.Id3v2.Frame frame)
        {
            if (frame == null || frame.FrameId == null)
                return null;

            string tagName = frame.FrameId.ToString();

            // TODO: Handle NXXX
            if (tagName == "TXXX") // Non-native (user-defined) tag.
            {
                var regex = new Regex("\\[.*\\]");
                var firstMatch = regex.Match(frame.ToString()).Value;

                //if (firstMatch.Length < 2) // There is no value between the brackets
                //    return null;

                tagName = firstMatch.Substring(1, firstMatch.Length - 2); // Trim the single pair of brackets surrounding title
            }

            bool success = Id3TagMap.TryGetValue(tagName, out string tag);
            return success
                ? tag
                : tagName;
        }

        private static string GetID3v2TagValue(TagLib.Id3v2.Frame frame)
        {
            // TODO: Handle NXXX
            if (frame.FrameId == "TXXX") // Non-native (user-defined) tag.
            {
                var regex = new Regex("\\] .*");
                var firstMatch = regex.Match(frame.ToString()).Value;
                var fieldValue = firstMatch.Substring(2); // Trim the leading bracket & space before value
                return fieldValue;
            }

            return frame.ToString();
        }

        public static readonly HashSet<string> IgnoredId3Tags = new HashSet<string>
        {
            "COMM",
            "PRIV",
        };

        public static readonly HashSet<string> SupportedExtensions = new HashSet<string>
        {
            ".mp3",
            ".flac",
            ".wav",
        };

        public static readonly IReadOnlyDictionary<string, string> Id3TagMap = new Dictionary<string, string>
        {
            { "TRCK", "TrackNumber" },
            { "TCON", "Genre"       },
            { "TIT2", "Title"       },
            { "TPE1", "Artist"      },
            { "TPE2", "AlbumArtist" },
            { "TALB", "Album"       },
            { "TDRC", "Date"        },
            { "COMM", "Comment"     },
            { "APIC", "Artwork"     },
            { "TENC", "EncodedBy"   },
            { "TCOM", "ComposedBy"  },
        };
    }
}
