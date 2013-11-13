//
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
        private const int MAX_ALTERNATES = 4;

#if CSV
        private static readonly CultureInfo CSV_OUTPUT_CULTURE = CultureInfo.InvariantCulture;
        private static readonly Encoding CSV_OUTPUT_ENCODING = new UTF8Encoding(false);
#endif

        private static readonly CultureInfo CLJ_OUTPUT_CULTURE = CultureInfo.InvariantCulture;
        private static readonly Encoding CLJ_OUTPUT_ENCODING = new UTF8Encoding(false);

        private const int GLOBAL_LIST_LENGTH = 10;
        private const int LOCAL_LIST_LENGTH = 5;

        private static string sourceFile;
        private static string inputName;
        private static string targetFile;

        private static AutoResetEvent exitEvent;

        private static double confidenceTestDuration = 3 * 60;
        private static long phraseCount;
        private static double phraseConfidenceSum;
        private static float minPhraseConfidence = 1f;
        private static float maxPhraseConfidence = 0f;
        private static long wordCount;
        private static double wordConfidenceSum;
        private static float minWordConfidence = 1f;
        private static float maxWordConfidence = 0f;

        private static Dictionary<string, WordStats> hitlist;

#if CSV
        private static TextWriter outPhrases;
        private static TextWriter outWords;
#endif
        private static SpeechRecognitionEngine engine;
        private static TextWriter outEdn;

        private static long resultNo;

        private static ProcessingMode procMode = ProcessingMode.Default;
        private static bool showDashboard;
        private static bool showProgress;
        private static bool exitWithError;

        /// <remarks>
        /// <para>
        /// Usage <c>Transcripter.exe [options] &lt;source file&gt;</c>
        /// </para>
        /// <para>
        /// <c>--dashboard</c>, <c>-db</c>:
        ///     Shows a detailed dashboard in default processing mode.
        /// </para>
        /// <para>
        /// <c>--confidence-test</c>, <c>-ct</c>:
        ///     Switches to confidence test mode.
        /// </para>
        /// <para>
        /// <c>--test-duration</c>, <c>-td &lt;seconds&gt;</c>:
        ///     Specifies the max time of confidence testing in seconds.
        /// </para>
        /// <para>
        /// <c>--progress</c>, <c>-p</c>:
        ///     Writes current progress as current position in audio stream to the standard output.
        ///     (Has no affect, if <c>--dashboard</c>/<c>-d</c> is used.)
        /// </para>
        /// <para>
        /// <c>--target</c>, <c>-t &lt;target file&gt;</c>:
        ///     specifies the path to the result file
        /// </para>
        /// </remarks>
        static int Main(string[] args)
        {
            var cla = new CommandLineArguments(args);
            if (!cla.HasArguments)
            {
                Console.WriteLine("You need to specify a filename.");
                return -1;
            }

            procMode = cla.HasSwitch("-ct", "--confidence-test")
                ? ProcessingMode.ConfidenceTest
                : ProcessingMode.Default;

            showDashboard = cla.HasSwitch("-db", "--dashboard");
            showProgress = cla.HasSwitch("-p", "--progress");

            confidenceTestDuration = cla.GetFloatingPoint("-td", "--test-duration") ?? confidenceTestDuration;

            sourceFile = cla.LastArgument;
            inputName = Path.GetFileNameWithoutExtension(sourceFile) ?? "unknown";
            targetFile = cla.GetString("-t", "--target")
                ?? Path.Combine(Path.GetDirectoryName(sourceFile) ?? "", "transcript", inputName);
            var outputPath = Path.GetDirectoryName(targetFile);
            if (outputPath != null && !Directory.Exists(outputPath))
            {
                Directory.CreateDirectory(outputPath);
            }

            hitlist = new Dictionary<string, WordStats>();
            phraseConfidenceSum = 0.0;
            phraseCount = 0;
            resultNo = 0;
            exitEvent = new AutoResetEvent(false);

            if (showDashboard)
            {
                Console.WriteLine(inputName);
                Console.WriteLine();
                Console.WriteLine("Starting transcription...");
            }
#if CSV
            using (outPhrases = new StreamWriter(targetFile + ".phrases.csv", false, CSV_OUTPUT_ENCODING))
            using (outWords = new StreamWriter(targetFile + ".words.csv", false, CSV_OUTPUT_ENCODING))
#endif
            engine = new SpeechRecognitionEngine(INPUT_LANGUAGE_CULTURE);

            if (procMode != ProcessingMode.ConfidenceTest)
            {
                outEdn = new StreamWriter(targetFile + ".srr", false, CLJ_OUTPUT_ENCODING);
                BeginWriterOutput();
            }

            engine.MaxAlternates = MAX_ALTERNATES;
            engine.SpeechRecognized += SpeechRecognizedHandler;
            engine.RecognizeCompleted += RecognizeCompletedHandler;

            engine.LoadGrammar(new DictationGrammar { Name = "Dictation Grammar" });

            engine.SetInputToWaveFile(sourceFile);
            engine.RecognizeAsync(RecognizeMode.Multiple);
            exitEvent.WaitOne();

            if (procMode != ProcessingMode.ConfidenceTest)
            {
                EndWriterOutput();
                outEdn.Dispose();
            }
            if (procMode == ProcessingMode.ConfidenceTest)
            {
                WriteConfidenceTestResults();
            }

            return exitWithError ? -1 : 0;
        }

#if CSV
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
#endif

        static void SpeechRecognizedHandler(object sender, SpeechRecognizedEventArgs e)
        {
            ProcessResult(e.Result);

            if (procMode == ProcessingMode.Default)
            {
#if CSV
                WriteWordStats();
#endif
                WriteResult(e.Result);

            }
            if (showDashboard)
            {
                ShowResult(e.Result);
            }
            else if (showProgress)
            {
                ShowProgress(e.Result);
            }

            if (procMode == ProcessingMode.ConfidenceTest &&
                e.Result != null && e.Result.Audio != null &&
                (e.Result.Audio.AudioPosition + e.Result.Audio.Duration).TotalSeconds >= confidenceTestDuration)
            {
                engine.RecognizeAsyncCancel();
            }
        }

        static void RecognizeCompletedHandler(object sender, RecognizeCompletedEventArgs e)
        {
            if (showDashboard)
            {
                Console.Clear();
                if (e.Error != null)
                {
                    Console.WriteLine("Error encountered, {0}: {1}",
                        e.Error.GetType().Name, e.Error.Message);
                }
                if (procMode != ProcessingMode.ConfidenceTest && e.Cancelled)
                {
                    Console.WriteLine("Operation cancelled.");
                }
                if (e.InputStreamEnded)
                {
                    Console.WriteLine("End of stream encountered.");
                }
                Console.WriteLine("Speech recognition finished.");
            }

            exitWithError = e.Error != null;

            exitEvent.Set();
        }

        #region EDN output

        private static void BeginWriterOutput()
        {
            outEdn.WriteLine("[");
        }

        private static void EndWriterOutput()
        {
            outEdn.WriteLine("]");
            outEdn.Flush();
        }

        private static void WriteResult(RecognitionResult result)
        {
            if (result.Audio == null) return;
#if CSV
            // Phrases to CSV
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
#endif

            // All to EDN Format
            outEdn.WriteLine("{");
            outEdn.WriteLine("  :no             {0},", resultNo.ToString(CLJ_OUTPUT_CULTURE));
            outEdn.WriteLine("  :start          {0},", result.Audio.AudioPosition.TotalSeconds.ToString(CLJ_OUTPUT_CULTURE));
            outEdn.WriteLine("  :duration       {0},", result.Audio.Duration.TotalSeconds.ToString(CLJ_OUTPUT_CULTURE));
            outEdn.WriteLine("  :confidence {0},", result.Confidence.ToString(CLJ_OUTPUT_CULTURE));
            outEdn.WriteLine("  :text           \"{0}\",", result.Text.Replace("\"", "\\\""));
            outEdn.WriteLine("  :words");
            WriteWords(result.Words, "    ");
#if ALTERNATES
            outEdn.WriteLine("  :alternates");
            outEdn.WriteLine("    [");
            var phraseNo = 0;
            foreach (var alternate in result.Alternates)
            {
                outEdn.WriteLine("      {");
                outEdn.WriteLine("        :no         {0},", phraseNo.ToString(CLJ_OUTPUT_CULTURE));
                outEdn.WriteLine("        :confidence {0},", alternate.Confidence.ToString(CLJ_OUTPUT_CULTURE));
                outEdn.WriteLine("        :text       \"{0}\",", alternate.Text.Replace("\"", "\\\""));
                outEdn.WriteLine("        :words");
                WriteWords(alternate.Words, "            ");
                outEdn.WriteLine("      }");

                phraseNo++;
            }
            outEdn.WriteLine("    ]");
#endif
            outEdn.WriteLine("}");

            // Finish result output
            resultNo++;
        }

        private static void WriteWords(IEnumerable<RecognizedWordUnit> words, string prefix)
        {
            var wordNo = 0;
            outEdn.WriteLine("{0}[", prefix);
            foreach (var word in words)
            {
                outEdn.WriteLine(
                    "{0}  {{ :no {1} :confidence {2} :text \"{3}\" :lexical-form \"{4}\" :pronunciation \"{5}\" }}",
                    prefix,
                    wordNo.ToString(CLJ_OUTPUT_CULTURE),
                    word.Confidence.ToString(CLJ_OUTPUT_CULTURE),
                    word.Text.Replace("\"", "\\\""),
                    word.LexicalForm.Replace("\"", "\\\""),
                    word.Pronunciation.Replace("\"", "\\\""));

                wordNo++;
            }
            outEdn.WriteLine("{0}]", prefix);
        }

        #endregion

        static void WriteConfidenceTestResults()
        {
            var fp = CultureInfo.InvariantCulture;
            Console.WriteLine();
            Console.WriteLine("PhraseCount=" + phraseCount.ToString(fp));
            Console.WriteLine("PhraseConfidenceSum=" + phraseConfidenceSum.ToString(fp));
            Console.WriteLine("MaxPhraseConfidence=" + maxPhraseConfidence.ToString(fp));
            Console.WriteLine("MeanPhraseConfidence=" + (phraseCount > 0 ? phraseConfidenceSum / phraseCount : 0.0).ToString(fp));
            Console.WriteLine("MinPhraseConfidence=" + minPhraseConfidence.ToString(fp));
            Console.WriteLine("WordCount=" + wordCount.ToString(fp));
            Console.WriteLine("WordConfidenceSum=" + wordConfidenceSum.ToString(fp));
            Console.WriteLine("MaxWordConfidence=" + maxWordConfidence.ToString(fp));
            Console.WriteLine("MeanWordConfidence=" + (wordCount > 0 ? wordConfidenceSum / wordCount : 0.0).ToString(fp));
            Console.WriteLine("MinWordConfidence=" + minWordConfidence.ToString(fp));

            // The best mean confidence of all occurences of a word.
            Console.WriteLine("BestWordConfidence=" + hitlist.Values.OrderBy(ws => -ws.MeanConfidence).FirstOrDefault().MeanConfidence.ToString(fp));
        }

        private static void ShowProgress(RecognitionResult result)
        {
            if (result.Audio == null) return;
            var pos = result.Audio.AudioPosition;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0:###0}:{1:00}:{2:00}.{3:000}",
                pos.Days * 24 + pos.Hours,
                pos.Minutes,
                pos.Seconds,
                pos.Milliseconds));
        }

        #region Dashboard Analytics

        static void ProcessResult(RecognizedPhrase result)
        {
            phraseCount++;
            phraseConfidenceSum += result.Confidence;
            minPhraseConfidence = Math.Min(minPhraseConfidence, result.Confidence);
            maxPhraseConfidence = Math.Max(maxPhraseConfidence, result.Confidence);

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
            wordCount++;
            wordConfidenceSum += word.Confidence;
            minWordConfidence = Math.Min(minWordConfidence, word.Confidence);
            maxWordConfidence = Math.Max(maxWordConfidence, word.Confidence);
            if (!IsNoun(word.Text)) return;
            var key = word.Text; //.ToLower(CULTURE);
            WordStats stat;
            hitlist.TryGetValue(key, out stat);
            hitlist[key] = stat.Update(word.Confidence);
        }

        static void ShowResult(RecognitionResult result)
        {
            Console.Title = inputName;
            Console.SetWindowSize(100, 16 + GLOBAL_LIST_LENGTH + LOCAL_LIST_LENGTH + 6);
            Console.Clear();
            Console.WriteLine(inputName);
            Console.WriteLine();
            Console.WriteLine("Global:");
            WriteConfidence("  Confidence", phraseConfidenceSum / phraseCount);
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

        #endregion
    }

    enum ProcessingMode
    {
        Default,
        ConfidenceTest
    }
}