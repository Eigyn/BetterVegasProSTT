using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Subtitler.Core
{
    // Replaces the letters of profane words with asterisks, keeping their length and any
    // punctuation: "What in the fucking hell?!" becomes "What in the ******* ****?!".
    // Whole words only, so hello, class, assess, cocktail and Scunthorpe are untouched.
    public static class Profanity
    {
        // Roots no ordinary English word contains: any word with one is profane.
        private static readonly string[] Roots = { "fuck", "shit", "bitch" };
        // Every other word is matched exactly, with its common forms listed.
        private static readonly HashSet<string> Words = new HashSet<string>(StringComparer.Ordinal) {
            "ass", "asses", "asshole", "assholes", "arse", "arsehole", "arseholes", "dumbass", "dumbasses", "jackass", "jackasses", "smartass", "badass",
            "bastard", "bastards", "bollocks", "bugger", "buggered",
            "cock", "cocks", "cocksucker", "cocksuckers", "crap", "crappy", "crapped", "cunt", "cunts",
            "damn", "damned", "damnit", "dammit", "goddamn", "goddamned", "goddamnit", "goddammit",
            "dick", "dicks", "dickhead", "dickheads", "douche", "douches", "douchebag", "douchebags",
            "fag", "fags", "faggot", "faggots", "hell", "jizz",
            "nigga", "niggas", "niggaz", "nigger", "niggers",
            "piss", "pissed", "pisses", "pissing", "prick", "pricks", "pussy", "pussies",
            "retard", "retards", "retarded", "slut", "sluts", "slutty",
            "spic", "spics", "chink", "chinks", "kike", "kikes", "tranny", "trannies",
            "tit", "tits", "titties", "twat", "twats", "wank", "wanker", "wankers", "wanking", "whore", "whores",
        };
        // A word, with inner apostrophes and hyphens: "mother-fucker", "bitch's".
        private static readonly Regex Word = new Regex(@"[\p{L}\p{N}]+(?:['’-][\p{L}\p{N}]+)*", RegexOptions.Compiled);
        private static readonly Regex Letter = new Regex(@"[\p{L}\p{N}]", RegexOptions.Compiled);

        public static string Censor(string text)
        {
            return text == null ? null : Word.Replace(text, m => {
                // A possessive keeps its 's: "the bastard's" becomes "the *******'s".
                var possessive = Possessive.Match(m.Value);
                string stem = possessive.Success ? possessive.Groups[1].Value : m.Value;
                return IsProfane(stem) ? Letter.Replace(stem, "*") + m.Value.Substring(stem.Length) : m.Value;
            });
        }
        private static readonly Regex Possessive = new Regex(@"^(.+)['’][sS]$", RegexOptions.Compiled);

        public static bool IsProfane(string word)
        {
            string plain = new string(word.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            return Roots.Any(plain.Contains) || Words.Contains(plain);
        }
    }
}
