using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace de.fhb.oll.transcripter
{
    class CommandLineArguments
    {
        private readonly string[] args;

        public CommandLineArguments(string[] args)
        {
            this.args = args;
        }

        public int Count { get { return args.Length; } }

        public bool HasArguments { get { return args.Length > 0; } }

        public string FirstArgument { get { return HasArguments ? args[0] : null; } }

        public string LastArgument { get { return HasArguments ? args[args.Length - 1] : null; } }

        public bool HasSwitch(params string[] switchNames)
        {
            return switchNames.Any(args.Contains);
        }

        public string GetString(params string[] argumentNames)
        {
            var pos = -1;
            for (var i = 0; i < args.Length; i++)
            {
                if (argumentNames.Any(an => string.Equals(an, args[i])))
                {
                    pos = i;
                    break;
                }
            }
            return pos >= 0 && args.Length > pos + 1 ? args[pos + 1] : null;
        }

        public long? GetInteger(params string[] argumentNames)
        {
            var str = GetString(argumentNames);
            if (string.IsNullOrWhiteSpace(str)) return null;
            long value;
            return long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                ? value : (long?)null;
        }

        public double? GetFloatingPoint(params string[] argumentNames)
        {
            var str = GetString(argumentNames);
            if (string.IsNullOrWhiteSpace(str)) return null;
            double value;
            return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                ? value : (double?)null;
        }

        public decimal? GetDecimal(params string[] argumentNames)
        {
            var str = GetString(argumentNames);
            if (string.IsNullOrWhiteSpace(str)) return null;
            decimal value;
            return decimal.TryParse(str,
                NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value)
                ? value : (decimal?)null;
        }
    }
}
