﻿//
// Copyright (c) 2013 Tobias Kiertscher <kiertscher@fh-brandenburg.de>.
// Alle Rechte vorbehalten.
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Speech.Recognition;
using System.Text;
using System.Threading;

namespace de.fhb.oll.transcripter
{
    class Program
    {
        private static readonly CultureInfo INPUT_LANGUAGE_CULTURE = CultureInfo.GetCultureInfo("de-DE");

        private static readonly CultureInfo CSV_OUTPUT_CULTURE = CultureInfo.InvariantCulture;
        private static readonly Encoding CSV_OUTPUT_ENCODING = new UTF8Encoding(false);

        private static readonly CultureInfo CLJ_OUTPUT_CULTURE = CultureInfo.InvariantCulture;
        private static readonly Encoding CLJ_OUTPUT_ENCODING = new UTF8Encoding(false);

        private const int GLOBAL_LIST_LENGTH = 10;
        private const int LOCAL_LIST_LENGTH = 5;

        private static string sourceFile;
        private static string inputName;
        private static string targetFile;

        private static AutoResetEvent exitEvent;
        private static double confidenceSum;
        private static long phraseCount;
        private static Dictionary<string, WordStats> hitlist;

        private static TextWriter outPhrases;
        private static TextWriter outWords;
        private static TextWriter outClojure;

        private static long resultNo;

        static void Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("You need to specify a filename.");
                return;
            }

            sourceFile = args[0];
            inputName = Path.GetFileNameWithoutExtension(sourceFile) ?? "unknown";
            hitlist = new Dictionary<string, WordStats>();
            confidenceSum = 0.0;
            phraseCount = 0;
            resultNo = 0;
            exitEvent = new AutoResetEvent(false);

            var outputPath = Path.Combine(Path.GetDirectoryName(sourceFile) ?? "", "transcript");
            if (!Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }
            targetFile = Path.Combine(outputPath, inputName);

            Console.WriteLine(inputName);
            Console.WriteLine();
            Console.WriteLine("Starting transcription...");

            using (outPhrases = new StreamWriter(targetFile + ".phrases.csv", false, CSV_OUTPUT_ENCODING))
            using (outWords = new StreamWriter(targetFile + ".words.csv", false, CSV_OUTPUT_ENCODING))
            using (outClojure = new StreamWriter(targetFile + ".clj", false, CLJ_OUTPUT_ENCODING))
            using (var engine = new SpeechRecognitionEngine(INPUT_LANGUAGE_CULTURE))
            {
                BeginWriterOutput();

                engine.SpeechRecognized += RecognizerSpeechRecognizedHandler;
                engine.RecognizeCompleted += RecognizerRecognizeCompletedHandler;

                Grammar dictation = new DictationGrammar();
                dictation.Name = "Dictation Grammar";
                engine.LoadGrammar(dictation);

                engine.SetInputToWaveFile(sourceFile);
                engine.RecognizeAsync(RecognizeMode.Multiple);
                exitEvent.WaitOne();

                EndWriterOutput();
            }
        }

        private static void BeginWriterOutput()
        {
            outClojure.WriteLine("[");
        }

        private static void EndWriterOutput()
        {
            outClojure.WriteLine("]");
            outClojure.Flush();
        }

        private static void WriteWordStats()
        {
            using (var outWordStats = new StreamWriter(targetFile + ".wordstats.csv", false, Encoding.UTF8))
            {
                outWordStats.WriteLine("# Count, ConfidenceSum, SquaredConfidenceSum, MeanConfidence, MeanSquaredConfidence, Word");
                foreach (var kvp in hitlist.OrderByDescending(kvp => kvp.Value.SquaredConfidenceSum))
                {
                    outWordStats.WriteLine("{0}, {1}, {2}, {3}, {4}, \"{5}\"",
                                           kvp.Value.Count.ToString(CSV_OUTPUT_CULTURE),
                                           kvp.Value.ConfidenceSum.ToString(CSV_OUTPUT_CULTURE),
                                           kvp.Value.SquaredConfidenceSum.ToString(CSV_OUTPUT_CULTURE),
                                           kvp.Value.MeanConfidence.ToString(CSV_OUTPUT_CULTURE),
                                           kvp.Value.MeanSquaredConfidance.ToString(CSV_OUTPUT_CULTURE),
                                           kvp.Key.Replace("\"", "\\\""));
                }
            }
        }

        static void RecognizerSpeechRecognizedHandler(object sender, SpeechRecognizedEventArgs e)
        {
            ProcessResult(e.Result);
            WriteWordStats();

            WriteResult(e.Result);

            ShowResult(e.Result);
        }

        private static void WriteResult(RecognitionResult result)
        {
            // Phrases to CSV
            if (result.Audio == null) return;
            outPhrases.WriteLine("{0}, {1}, \"{2}\"",
                                 result.Audio.AudioPosition.ToString("G", CSV_OUTPUT_CULTURE),
                                 result.Audio.Duration.ToString("G", CSV_OUTPUT_CULTURE),
                                 result.Text.Replace("\"", "\\\""));
            outPhrases.Flush();

            // Words to CSV
            var words = new HashSet<Tuple<string, float>>(
                result.Alternates
                 .SelectMany(a => a.Words)
                 .Select(w => Tuple.Create(w.Text, w.Confidence)));
            foreach (var tuple in words)
            {
                outWords.WriteLine("{0}, \"{1}\"",
                                   tuple.Item2.ToString(CSV_OUTPUT_CULTURE),
                                   tuple.Item1.Replace("\"", "\\\""));
            }

            // All to Clojure Format
            outClojure.WriteLine("{");
            outClojure.WriteLine("  :start          \"{0}\",", result.Audio.AudioPosition.ToString("G", CLJ_OUTPUT_CULTURE));
            outClojure.WriteLine("  :duration       \"{0}\",", result.Audio.Duration.ToString("G", CLJ_OUTPUT_CULTURE));
            outClojure.WriteLine("  :no             {0},", resultNo.ToString(CLJ_OUTPUT_CULTURE));
            outClojure.WriteLine("  :max-confidence {0},", result.Confidence.ToString(CLJ_OUTPUT_CULTURE));
            outClojure.WriteLine("  :text           \"{0}\",", result.Text.Replace("\"", "\\\""));
            outClojure.WriteLine("  :alternates");
            outClojure.WriteLine("    [");
            var phraseNo = 0;
            foreach (var alternate in result.Alternates)
            {
                outClojure.WriteLine("      {");
                outClojure.WriteLine("        :no         {0},", phraseNo.ToString(CLJ_OUTPUT_CULTURE));
                outClojure.WriteLine("        :confidence {0},", alternate.Confidence.ToString(CLJ_OUTPUT_CULTURE));
                outClojure.WriteLine("        :text       \"{0}\",", alternate.Text.Replace("\"", "\\\""));
                outClojure.WriteLine("        :words");
                outClojure.WriteLine("          [");
                var wordNo = 0;
                foreach (var word in result.Words)
                {
                    outClojure.WriteLine("            {{ :no {0} :confidence {1} :text \"{2}\" :lexical-form \"{3}\" :pronunciation \"{4}\" }}",
                        wordNo.ToString(CLJ_OUTPUT_CULTURE),
                        word.Confidence.ToString(CLJ_OUTPUT_CULTURE),
                        word.Text.Replace("\"", "\\\""),
                        word.LexicalForm.Replace("\"", "\\\""),
                        word.Pronunciation.Replace("\"", "\\\""));

                    wordNo++;
                }
                outClojure.WriteLine("          ]");
                outClojure.WriteLine("      }");

                phraseNo++;
            }
            outClojure.WriteLine("    ]");
            outClojure.WriteLine("}");

            // Finish result output
            resultNo++;
        }

        static void RecognizerRecognizeCompletedHandler(object sender, RecognizeCompletedEventArgs e)
        {
            Console.Clear();
            if (e.Error != null)
            {
                Console.WriteLine("Error encountered, {0}: {1}",
                e.Error.GetType().Name, e.Error.Message);
            }
            if (e.Cancelled)
            {
                Console.WriteLine("Operation cancelled.");
            }
            if (e.InputStreamEnded)
            {
                Console.WriteLine("End of stream encountered.");
            }
            Console.WriteLine("Recognize complete.");

            exitEvent.Set();
        }

        static void ProcessResult(RecognitionResult result)
        {
            confidenceSum += result.Confidence;
            phraseCount++;
            foreach (var word in result.Words) ProcessWord(word);
        }

        struct WordStats
        {
            public static readonly WordStats Empty = new WordStats(0, 0, 0);

            public readonly int Count;
            public readonly double ConfidenceSum;
            public readonly double SquaredConfidenceSum;

            private WordStats(int count, double confidenceSum, double squaredConfidenceSum)
            {
                Count = count;
                ConfidenceSum = confidenceSum;
                SquaredConfidenceSum = squaredConfidenceSum;
            }

            public WordStats Update(double confidence)
            {
                return new WordStats(
                    Count + 1,
                    ConfidenceSum + confidence,
                    SquaredConfidenceSum + Math.Pow(confidence, 2));
            }

            public double MeanConfidence { get { return ConfidenceSum / Count; } }

            public double MeanSquaredConfidance { get { return SquaredConfidenceSum / Count; } }
        }

        static void ProcessWord(RecognizedWordUnit word)
        {
            if (!IsNoun(word.Text)) return;
            var key = word.Text; //.ToLower(CULTURE);
            WordStats stat;
            hitlist.TryGetValue(key, out stat);
            hitlist[key] = stat.Update(word.Confidence);
        }

        static void ShowResult(RecognitionResult result)
        {
            Console.SetWindowSize(100, 16 + GLOBAL_LIST_LENGTH + LOCAL_LIST_LENGTH + 6);
            Console.Clear();
            Console.WriteLine(inputName);
            Console.WriteLine();
            Console.WriteLine("Global:");
            WriteConfidence("  Confidence", confidenceSum / phraseCount);
            Console.WriteLine();
            Console.WriteLine("  Hitlist:");
            var hits = hitlist
                .OrderByDescending(p => p.Value.SquaredConfidenceSum)
                .Take(GLOBAL_LIST_LENGTH)
                .ToArray();
            for (var i = 0; i < GLOBAL_LIST_LENGTH; i++)
            {
                var hit = i < hits.Length
                    ? hits[i]
                    : new KeyValuePair<string, WordStats>("-", WordStats.Empty);

                Console.WriteLine("    {0:D2}. {1:0000} {2:00.0 %}: {3}",
                    i + 1, hit.Value.Count, hit.Value.MeanConfidence, hit.Key);
            }
            Console.WriteLine();

            Console.WriteLine("Current Phrase:");
            if (result.Audio != null)
            {
                Console.WriteLine("    Time:       " + result.Audio.AudioPosition.ToString("c", CultureInfo.CurrentUICulture));
                Console.WriteLine("    Duration:   " + result.Audio.Duration.ToString("c", CultureInfo.CurrentUICulture));
            }
            WriteConfidence("    Confidence", result.Confidence);
            Console.WriteLine();

            Console.WriteLine("  Words:");
            var words = result.Words
                .Where(rwu => IsNoun(rwu.Text))
                .OrderByDescending(rwu => rwu.Confidence)
                .Take(LOCAL_LIST_LENGTH)
                .ToArray();
            for (var i = 0; i < LOCAL_LIST_LENGTH; i++)
            {
                var word = i < words.Length ? words[i] : null;
                Console.WriteLine("    {0:D2}. {1:00.0 %}: {2}",
                    i + 1,
                    word != null ? word.Confidence : 0,
                    word != null ? words[i].Text : "-");
            }
            Console.WriteLine();

            Console.WriteLine("Recognized:");
            Console.WriteLine();
            Console.WriteLine(result.Text);

            Console.Out.Flush();
            Console.SetWindowPosition(0, 0);
        }

        static void WriteConfidence(string label, double value)
        {
            var w = Console.WindowWidth - label.Length - 12;
            var n = (int)Math.Round(w * value);
            var bar = "".PadRight(n, '#').PadRight(w);
            Console.WriteLine("{0}: |{1}| {2:00.0 %}", label, bar, value);
        }

        private static bool IsNoun(string word)
        {
            if (word == null) return false;
            word = word.Trim();
            if (word.Length < 2) return false;
            if (word.EndsWith(".")) return false;
            if (BLACKLIST.Contains(word.ToLower(INPUT_LANGUAGE_CULTURE))) return false;
            var firstChar = word.Substring(0, 1);
            return firstChar.ToLower() != firstChar;
        }

        static readonly HashSet<string> BLACKLIST = new HashSet<string>
            {
                ".", ",", "!", "?", ":", ";",
                "der", "die", "das", "des", "dieser", "diese", "dieses", "diesen", "diesem", 
                "dass",
                "den", "dem", "denen",
                "jenes", "jene", "jener", "derjenige", "diejenige", "dasjenige",
                "sein", "seiner", "seine", "seines", "seinen", "seinem",
                "ihr", "ihrer", "ihres", "ihren", "ihrem",
                "deren",
                "etwas", "jemand", "jemandem", "jemandes",
                "jede", "jeder", "jedes", "alle",
                "kein", "keine", "keiner", "keines", 
                "niemand", "niemanden", "niemandes",
                "derselbe", "dieselbe", "dasselbe",
                "ich", "du", "er", "sie", "es", "wir", "ihr", "sie",
                "mein", "dein", "sein", "ihr", "uns", "euch",
                "meiner", "deiner", "seiner", "ihrer", "unser", "euerer",
                "meine", "deine", "ihre", "seine", "unsere", "eure",
                "meines", "meins", "deines", "ihres", "ihrs", "seines", "seins", "unseres", "unsers", "eures", "euers",
                "meinen", "deinen", "seinen", "ihren", "euren",
                "meinem", "deinem", "seinem", "ihrem", "eurem",
                "mir", "dir", "ihm", "ihr", "uns", "euch", "ihnen",
                "mich", "dich", "ihn",
                "sich", "man",
                "ein", "einer", "eine", "eines", "einen", "einem",
                "mancher", "manche", "manches",
                "wer", "wen", "wem", "welche", "welcher", "wessen",
                "was", "welchen", "welches",
                "warum", "weshalb", "weswegen",
                "wie", "wieso", "so",
                "wo", "wofür", "wozu", "womit", "wodurch", "worum", "worüber", "wohin", "woher",
                "da", "dafür", "dazu", "damit", "dadurch", "darum", "darüber", "dahin", "daher",
                "woran", "worin", "worauf", "worunter", "wovor", "wohinter",
                "daran", "darin", "darauf", "darunter", "davor", "dahinter",
                "wann", "dann", "wenn",

                "am", "auf", "außer", "bei", "gegenüber", "vor", "hinter", "in", "für", 
                "neben", "zwischen", "an", "zu", "mit", "durch", "über", "unter",
                "über", "zur", "zum", "von", "gegen", "nur", "voran", "nach", "wegen", "nach",
                "abseits", "außerhalb", "diesseits", "innerhalb", "inmitten", "entlang", 
                "jenseits", "längs", "oberhalb", "unterhalb", "unweit", "unterm", "überm",
                "ab", "bis", "binnen", "seit", "während",
                "angesichts", "anlässlich", "aufgrund", "aus", "betreffs", "bezüglich",
                "durch", "gemäß", "halber", "infolge", "mangels", "mittels", "ob",
                "seitens", "trotz", "um", "ungeachtet", "vermöge", "wegen", "zufolge",
                "zwecks", "abzüglich", "ausschließlich", "außer", "einschließlich", "entgegen", 
                "exklusive", "inklusive", "mitsamt", "nebst", "ohne", "statt", "anstatt", "wieder", 
                "zuwider", "zuzüglich", "weil", "deshalb", "sogar", "doch", "nicht", "nichts", "direkt", "indirekt",
                "und", "oder", "aber", "hier", "da", "oben", "unten", "hinten", "vorn", "vorne", 
                "seitlich", "seitwärts", "umgekehrt", "auch", "ganz", "halb",
                "immer", "niemals", "ständig", "noch", "jetzt", "mal", "gut", "schlecht", "immer", "wieder",
                "oft", "öfter", "mehrfach", "wiederholt", "als", "sehr", "obwohl", "obschon",
                "klar", "unklar", "vollständig", "unvollständig", "noch", "fast", "kaum", "nie", 

                "andere", "anderer", "anderes", "anderen",
                "groß", "größer", "größte", "größere", "größeres", "größeren", "größerem", "größter", "größte", "größtes", "größten", "größtem",
                "klein", "kleiner", "kleinere", "kleinerer", "kleineres", "kleineren", "kleinerem", "kleinster", "kleinstes", "kleinste", "kleinsten", "kleinstem",
                "spät", "später", "spätestens",
                "früh", "früher", "frühestens",
                "gleich", "gleiche", "gleicher", "gleiches", "gleichen", "gleichem",
                "mehr", "mehrere", "mehreren", "mehrerem",
                "weniger", "wenigstens", "wenigster", "wenigste", "wenigstes", "wenigesten", "wenigestem",
                "alle", "aller", "alles", "allen", "allem",
                "kurz", "kürzer", "kürzester", "kürzeste", "kürstestes", "kürzesten", "kürzestem",
                "lang", "länger", "längster", "längste", "längstes", "längsten", "längstem",
                "solch", "solche", "solcher", "solches", "solchen", "solchem",
                "jeder", "jede", "jedes", "jeden", "jedem",

                "seien", "sein", "bin", "ist", "sind", "war", "waren", "wäre", "wären",
                "werden", "bin", "seid", "wurde", "wurden", "würde", "würden", "wird", "werdet", "werde",
                "worden", "geworden",
                "hat", "haben", "hatte", "hatten", "habe", "hätte", "hätten",
                "kann", "können", "konnte", "konnten", "könnte", "könnten",
                "mag", "mögen", "mochte", "mochten", 
                "will", "wollen", "wollte", "wollten",
                "soll", "sollen", "sollte", "sollten",
                "muss", "müssen", "musste", "mussten",
                "gebe", "gibt", "geben", "gebt", "gab", "gaben",
                "kommen", "komme", "kommt", "kommen", "kam", "kamen",
                "gehen", "gehe", "geht", "gehen", "ging", "gingen",
                "machen", "mache", "macht", "machte", "machten",

                "null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun", "zehn",
                "elf", "zwölf", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig", "achtzig", "neunzig",
                "nullmal", "einmal", "zweimal", "dreimal", "viermal", "fünfmal", "sechsmal", "siebenmal", "achtmahl", "neunmahl", "zehnmal",
                "erste", "zweite", "dritte", "vierte", "fünfte", "sechste", "siebte", "achte", "neunte", "zehnte",
                "erster", "zweiter", "dritter", "vierter", "fünfter", "sechster", "siebter", "achter", "neunter", "zehnter",
                "erstes", "zweites", "drittes", "viertes", "fünftes", "sechstes", "siebtes", "achtes", "neuntes", "zehntes",
                "ersten", "zweiten", "dritten", "vierten", "fünften", "sechsten", "siebten", "achten", "neunten", "zehnten",
                "erstem", "zweitem", "drittem", "viertem", "fünftem", "sechstem", "siebtem", "achtem", "neuntem", "zehntem",
                "hälfte", "drittel", "viertel", "fünftel", "sechstel", "siebtel", "achtel", "neuntel", "zehntel",
                "hundert", "tausend", "million", "milliarden", "billionen", "billiarden",
                "hunderte", "tausende", "millionen",
            };
    }
}