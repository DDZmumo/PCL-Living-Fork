// SPDX-License-Identifier: MPL-2.0
// Copyright (c) 2026 PCL Living Fork contributors.
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
#nullable disable
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Microsoft.VisualBasic.CompilerServices;

namespace PCLCS;
// Full LF declarations at 50b028da5fcad2226d74d901a41d8c43a249bfb2 (MPL-2.0 for PCLLF-original code).
// PCL/CE fixed-source provenance remains in Contracts; no CE policy imported.
public static class ModDependencyVersions {
        public static ModDependencyMatchState Combine(global::System.Collections.Generic.IEnumerable<ModDependencyMatchState> states, global::System.Boolean all)
        {
            ModDependencyMatchState[] Values = states.ToArray();
            if (((Values.Length) == (0)))
                return ModDependencyMatchState.Unknown;
            if (all)
            {
                if (Values.Contains(ModDependencyMatchState.Unsatisfied))
                    return ModDependencyMatchState.Unsatisfied;
                return (Values.Contains(ModDependencyMatchState.Unknown)) ? (ModDependencyMatchState.Unknown) : (ModDependencyMatchState.Satisfied);
            }
            if (Values.Contains(ModDependencyMatchState.Satisfied))
                return ModDependencyMatchState.Satisfied;
            return (Values.Contains(ModDependencyMatchState.Unknown)) ? (ModDependencyMatchState.Unknown) : (ModDependencyMatchState.Unsatisfied);
        }

        // Three-state version comparison. Unknown is deliberate and must never become a startup error.
        public static ModDependencyMatchState MatchVersion(global::System.String actual, global::System.String requirement, global::System.String syntax = "fabric")
        {
            return MatchVersions(actual, new global::Newtonsoft.Json.Linq.JValue(requirement), syntax);
        }
        public static ModDependencyMatchState MatchVersions(global::System.String actual, global::Newtonsoft.Json.Linq.JToken requirement, global::System.String syntax, global::System.Int32 depth = 0)
        {
            if ((((depth) > (32)) || requirement is null))
                return ModDependencyMatchState.Unknown;
            if (requirement is global::Newtonsoft.Json.Linq.JArray)
                return Combine(((global::Newtonsoft.Json.Linq.JArray)(requirement)).Select(Value => MatchVersions(actual, Value, syntax, (depth) + (1))), false);
            if (requirement is global::Newtonsoft.Json.Linq.JObject)
            {
                var Any = requirement["any"];
                var All = requirement["all"];
                if ((((Any is null)) == ((All is null))))
                    return ModDependencyMatchState.Unknown;
                var Values = (Any) ?? (All);
                if (!(Values is global::Newtonsoft.Json.Linq.JArray))
                    return ModDependencyMatchState.Unknown;
                return Combine(((global::Newtonsoft.Json.Linq.JArray)(Values)).Select(Value => MatchVersions(actual, Value, syntax, (depth) + (1))), All is not null);
            }
            if ((requirement.Type != global::Newtonsoft.Json.Linq.JTokenType.String))
                return ModDependencyMatchState.Unknown;
            global::System.String Range = requirement.ToString().Trim();
            if ((string.IsNullOrEmpty(Range) || (Range == "*")))
                return ModDependencyMatchState.Satisfied;
            if ((((((global::System.String.IsNullOrWhiteSpace(actual)) || (actual == "未知版本")) || (actual.Equals("unknown", global::System.StringComparison.OrdinalIgnoreCase))) || (actual.Contains("$" + "{"))) || (Range.Contains("$" + "{"))))
                return ModDependencyMatchState.Unknown;
            if ((syntax == "maven"))
                return MatchMaven(actual, Range);
            if (Range.Contains("||"))
                return Combine(Range.Split(new string[] { "||" }, global::System.StringSplitOptions.None).Select(Value => MatchVersion(actual, Value, syntax)), false);
            Range = global::System.Text.RegularExpressions.Regex.Replace(Range, @"([><=~^]+)\s+", "$1");
            global::System.String[] Tokens = global::System.Text.RegularExpressions.Regex.Split(Range, @"\s+").Where(Value => !string.IsNullOrEmpty(Value)).ToArray();
            if (Tokens.Any(Value => !(global::System.Text.RegularExpressions.Regex.IsMatch(Value, @"^(?:>=|<=|>|<|=|~|\^)?(?:[A-Za-z0-9][A-Za-z0-9._:+*-]*|\*)$"))))
                return ModDependencyMatchState.Unknown;
            return Combine(Tokens.Select(Value => MatchPredicate(actual, Value, syntax)), true);
        }
        private class NumericVersion
        {
            public global::System.Int64[] Parts;
            public global::System.String Prerelease;
        }
        private static NumericVersion ParseVersion(global::System.String Value)
        {
            var Match = global::System.Text.RegularExpressions.Regex.Match((Value) ?? (""), @"^(\d+(?:\.\d+)*)(?:-((?:[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?))?(?:\+[0-9A-Za-z.-]+)?$");
            if (!(Match.Success))
                return (NumericVersion)null;
            var Parts = new global::System.Collections.Generic.List<global::System.Int64>();
            foreach (var Part in Match.Groups[1].Value.Split('.'))
            {
                global::System.Int64 Number;
                if (!(global::System.Int64.TryParse(Part, global::System.Globalization.NumberStyles.None, global::System.Globalization.CultureInfo.InvariantCulture, out Number)))
                    return (NumericVersion)null;
                Parts.Add(Number);
            }
            // Fabric 的空预发布键（例如 1.21-）与正式版 1.21 不同，用作该版本所有预发布的下界。
            return new NumericVersion() { Parts = Parts.ToArray(), Prerelease = (Match.Groups[2].Success) ? (Match.Groups[2].Value) : (global::System.String)(null) };
        }
        private static global::System.Int32 CompareVersions(NumericVersion Left, NumericVersion Right)
        {
            for (global::System.Int32 Index = 0, loopTo = (global::System.Math.Max(Left.Parts.Length, Right.Parts.Length)) - (1); Index <= loopTo; Index++)
            {
                global::System.Int32 Comparison = (((Index) < (Left.Parts.Length)) ? (Left.Parts[Index]) : (0L)).CompareTo(((Index) < (Right.Parts.Length)) ? (Right.Parts[Index]) : (0L));
                if (((Comparison) != (0)))
                    return Comparison;
            }
            if (global::System.String.Equals(Left.Prerelease, Right.Prerelease, global::System.StringComparison.Ordinal))
                return 0;
            if (Left.Prerelease is null)
                return 1;
            if (Right.Prerelease is null)
                return -(1);
            if (string.IsNullOrEmpty(Left.Prerelease))
                return -(1);
            if (string.IsNullOrEmpty(Right.Prerelease))
                return 1;
            global::System.String[] LeftParts = Left.Prerelease.Split('.');
            global::System.String[] RightParts = Right.Prerelease.Split('.');
            for (global::System.Int32 Index = 0, loopTo1 = (global::System.Math.Min(LeftParts.Length, RightParts.Length)) - (1); Index <= loopTo1; Index++)
            {
                global::System.Int64 LeftNumber;
                global::System.Int64 RightNumber;
                global::System.Boolean LeftNumeric = global::System.Int64.TryParse(LeftParts[Index], out LeftNumber);
                global::System.Boolean RightNumeric = global::System.Int64.TryParse(RightParts[Index], out RightNumber);
                global::System.Int32 Comparison;
                if (((LeftNumeric) && (RightNumeric)))
                {
                    Comparison = LeftNumber.CompareTo(RightNumber);
                }
                else if (((LeftNumeric) != (RightNumeric)))
                {
                    Comparison = (LeftNumeric) ? (-(1)) : (1);
                }
                else
                {
                    Comparison = global::System.StringComparer.Ordinal.Compare(LeftParts[Index], RightParts[Index]);
                }
                if (((Comparison) != (0)))
                    return Comparison;
            }
            return LeftParts.Length.CompareTo(RightParts.Length);
        }
        private static ModDependencyMatchState MatchPredicate(global::System.String Actual, global::System.String Predicate, global::System.String Syntax)
        {
            if ((((Predicate == "*") || (Predicate == "x")) || (Predicate == "X")))
                return ModDependencyMatchState.Satisfied;
            var Match = global::System.Text.RegularExpressions.Regex.Match(Predicate, @"^(>=|<=|>|<|=|~|\^)?(.+)$");
            if (!(Match.Success))
                return ModDependencyMatchState.Unknown;
            global::System.String OperatorText = Match.Groups[1].Value;
            global::System.String Expected = Match.Groups[2].Value;
            var Current = ParseVersion(Actual);
            if (global::System.Text.RegularExpressions.Regex.IsMatch(Expected, @"^(?:\d+\.)*(?:\*|[xX])$"))
            {
                if ((!string.IsNullOrEmpty(OperatorText) && (OperatorText != "=")))
                    return ModDependencyMatchState.Unknown;
                if (Current is null)
                    return ModDependencyMatchState.Unknown;
                global::System.String[] Prefix = Expected.Split('.').TakeWhile(Part => (((Part != "*") && (Part != "x")) && (Part != "X"))).ToArray();
                for (global::System.Int32 Index = 0, loopTo = (Prefix.Length) - (1); Index <= loopTo; Index++)
                {
                    global::System.Int64 Number;
                    if (!(global::System.Int64.TryParse(Prefix[Index], out Number)))
                        return ModDependencyMatchState.Unknown;
                    if ((((((Index) < (Current.Parts.Length)) ? (Current.Parts[Index]) : (0L))) != (Number)))
                        return ModDependencyMatchState.Unsatisfied;
                }
                return ModDependencyMatchState.Satisfied;
            }
            var Target = ParseVersion(Expected);
            if ((Current is null || Target is null))
            {
                if ((!string.IsNullOrEmpty(OperatorText) && (OperatorText != "=")))
                    return ModDependencyMatchState.Unknown;
                if ((((!(global::System.Text.RegularExpressions.Regex.IsMatch(Expected, "^[A-Za-z0-9][A-Za-z0-9._:+-]*$"))) || (Expected.EndsWith("+", global::System.StringComparison.Ordinal))) || (new[] { "latest", "latest.release", "latest.integration" }.Contains(Expected.ToLowerInvariant()))))
                    return ModDependencyMatchState.Unknown;
                return (global::System.String.Equals(Actual, Expected, global::System.StringComparison.Ordinal)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
            }
            global::System.Int32 Comparison = CompareVersions(Current, Target);
            // Quilt's unprefixed semantic version means compatibility to the
            // next major; Fabric's unprefixed version remains exact equality.
            if (Syntax == "quilt" && string.IsNullOrEmpty(OperatorText)) OperatorText = "^";
            switch ((OperatorText ?? ""))
            {
                case var @case when @case == "":
                case "=":
                    {
                        return ((Comparison) == (0)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
                    }
                case ">":
                    {
                        return ((Comparison) > (0)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
                    }
                case ">=":
                    {
                        return ((Comparison) >= (0)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
                    }
                case "<":
                    {
                        return ((Comparison) < (0)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
                    }
                case "<=":
                    {
                        return ((Comparison) <= (0)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
                    }
                case "~":
                case "^":
                    {
                        if ((Syntax == "fabric" || Syntax == "quilt"))
                        {
                            // Both loaders compare equal major (~ also minor), including major zero and omitted minor components.
                            for (global::System.Int32 Index = 0, loopTo1 = (OperatorText == "~") ? (1) : (0); Index <= loopTo1; Index++)
                            {
                                if ((((((Index) < (Current.Parts.Length)) ? (Current.Parts[Index]) : (0L))) != ((((Index) < (Target.Parts.Length)) ? (Target.Parts[Index]) : (0L)))))
                                    return ModDependencyMatchState.Unsatisfied;
                            }
                            return ((Comparison) >= (0)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
                        }
                        global::System.Int64[] Upper = ((global::System.Int64[])(Target.Parts.Clone()));
                        global::System.Int32 Bump;
                        if ((OperatorText == "~"))
                        {
                            Bump = ((Upper.Length) > (1)) ? (1) : (0);
                        }
                        else
                        {
                            Bump = 0;
                            while ((((Bump) < ((Upper.Length) - (1))) && ((Upper[Bump]) == 0L)))
                                Bump += 1;
                        }
                        if (((Upper[Bump]) == (global::System.Int64.MaxValue)))
                            return ModDependencyMatchState.Unknown;
                        Upper[Bump] += 1L;
                        for (global::System.Int32 Index = (Bump) + (1), loopTo2 = (Upper.Length) - (1); Index <= loopTo2; Index++)
                            Upper[Index] = 0L;
                        var UpperVersion = new NumericVersion() { Parts = Upper, Prerelease = (global::System.String)null };
                        return (((Comparison) >= (0)) && ((CompareVersions(Current, UpperVersion)) < (0))) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied);
                    }

                default:
                    {
                        return ModDependencyMatchState.Unknown;
                    }
            }
        }
        private static bool TryCompareOrderedMaven(string left, string right, out int comparison) {
            comparison = 0;
            const string supported = @"^\d+(?:\.\d+)*(?:-(?:alpha|beta|milestone|rc|cr|snapshot)(?:[.-]?\d+)?)?$";
            if (!Regex.IsMatch(left, supported, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                !Regex.IsMatch(right, supported, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
            var first = ParseVersion(left); var second = ParseVersion(right);
            if (first is null || second is null) return false;
            comparison = CompareVersions(new NumericVersion { Parts = first.Parts }, new NumericVersion { Parts = second.Parts });
            if (comparison != 0) return true;
            if (first.Prerelease is null) comparison = second.Prerelease is null ? 0 : 1;
            else if (second.Prerelease is null) comparison = -1;
            else if (!string.Equals(first.Prerelease, second.Prerelease, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        private static ModDependencyMatchState MatchMaven(global::System.String Actual, global::System.String Range)
        {
            // Maven's bare version is a soft preference, not an exact constraint.
            if (((!(Range.StartsWith("[", global::System.StringComparison.Ordinal))) && (!(Range.StartsWith("(", global::System.StringComparison.Ordinal)))))
                return ModDependencyMatchState.Unknown;
            var Intervals = global::System.Text.RegularExpressions.Regex.Matches(Range, @"[\[\(][^\[\]\(\)]*[\]\)]");
            if ((((Intervals.Count) == (0)) || !string.IsNullOrEmpty(global::System.Text.RegularExpressions.Regex.Replace(Range, @"[\[\(][^\[\]\(\)]*[\]\)]", "").Trim(' ', ','))))
                return ModDependencyMatchState.Unknown;
            var Values = new global::System.Collections.Generic.List<ModDependencyMatchState>();
            foreach (global::System.Text.RegularExpressions.Match Interval in Intervals)
            {
                global::System.String Raw = Interval.Value;
                global::System.String Body = Raw.Substring(1, (Raw.Length) - (2));
                global::System.String[] Parts = Body.Split(',');
                if (((Parts.Length) == (1)))
                {
                    if ((((Raw[0]) != ('[')) || ((Raw.Last()) != (']'))))
                    {
                        Values.Add(ModDependencyMatchState.Unknown);
                    }
                    else
                    {
                        if (!TryCompareOrderedMaven(Actual, Parts[0].Trim(), out int equalComparison))
                        {
                            Values.Add((global::System.String.Equals(Actual, Parts[0].Trim(), global::System.StringComparison.Ordinal)) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unknown));
                        }
                        else
                        {
                            Values.Add(equalComparison == 0 ? ModDependencyMatchState.Satisfied : ModDependencyMatchState.Unsatisfied);
                        }
                    }
                    continue;
                }
                // Known prereleases have a proven relation to numeric releases.
                // Distinct qualifier spellings/orders stay deliberately unknown.
                if (Parts.Length != 2)
                {
                    Values.Add(ModDependencyMatchState.Unknown);
                    continue;
                }
                var Bounds = new global::System.Collections.Generic.List<ModDependencyMatchState>();
                for (global::System.Int32 Index = 0; Index <= 1; Index++)
                {
                    global::System.String Bound = Parts[Index].Trim();
                    if (string.IsNullOrEmpty(Bound))
                    {
                        Bounds.Add(ModDependencyMatchState.Satisfied);
                        continue;
                    }
                    if (!TryCompareOrderedMaven(Actual, Bound, out int Compare))
                    {
                        Bounds.Add(ModDependencyMatchState.Unknown);
                        continue;
                    }
                    global::System.Boolean Fits = ((Index) == (0)) ? (((Compare) > (0)) || (((((Compare) == (0)) && ((Raw[0]) == ('[')))))) : (((Compare) < (0)) || (((((Compare) == (0)) && ((Raw.Last()) == (']'))))));
                    Bounds.Add((Fits) ? (ModDependencyMatchState.Satisfied) : (ModDependencyMatchState.Unsatisfied));
                }
                Values.Add(Combine((global::System.Collections.Generic.IEnumerable<ModDependencyMatchState>)Bounds, true));
            }
            return Combine((global::System.Collections.Generic.IEnumerable<ModDependencyMatchState>)Values, false);
        }

}
