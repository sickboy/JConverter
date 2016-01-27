﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using NDepend.Path;

namespace JConverter
{
    public class MplusConverter
    {
        private readonly Config _config;
        private SpssDataTransformer _spssDataTransformer;

        public MplusConverter(IAbsoluteFilePath inFile, Config config)
        {
            Contract.Requires<ArgumentNullException>(inFile != null);
            Contract.Requires<ArgumentNullException>(config != null);
            _config = config;
            InFile = inFile;
            OutDatFile = GenerateOutFilePath(".dat");
            OutInpFile = GenerateOutFilePath(".inp");
        }

        public IAbsoluteFilePath OutInpFile { get; }

        public IAbsoluteFilePath OutDatFile { get; }

        public IAbsoluteFilePath InFile { get; }

        private IAbsoluteFilePath GenerateOutFilePath(string ext)
        {
            var inFileProcessed = InFile.ToString().Replace(" ", "_");
            return (inFileProcessed + ext).ToAbsoluteFilePath();
        }

        public void ProcessFile()
        {
            ConfirmInputFileExists();
            ConfirmOutFilesDontExist();
            var data = ParseAndTransformData();
            CreateTransformedDatFile(data);
            CreateInpFile();
        }

        private void ConfirmInputFileExists()
        {
            if (!InFile.Exists) throw new FileNotFoundException("The file does not exist: " + InFile);
        }

        private void ConfirmOutFilesDontExist()
        {
            if (OutDatFile.Exists)
                throw new InvalidOperationException("The .dat file already exists, please delete it first: " +
                                                    OutDatFile);
            if (OutInpFile.Exists)
                throw new InvalidOperationException("The .inp file already exists, please delete it first: " +
                                                    OutInpFile);
        }

        private IEnumerable<string> ParseAndTransformData()
        {
            var data = ReadInputFile();
            _spssDataTransformer = new SpssDataTransformer(_config, data);
            _spssDataTransformer.TransformData();
            return data;
        }

        private string[] ReadInputFile() => File.ReadAllLines(InFile.ToString());

        private void CreateTransformedDatFile(IEnumerable<string> data)
            => File.WriteAllText(OutDatFile.ToString(), GenerateTransformedDatData(data));

        private string GenerateTransformedDatData(IEnumerable<string> data)
            => string.Join(_config.NewLine, data.Where(x => x != null));

        private void CreateInpFile() => File.WriteAllText(OutInpFile.ToString(), GenerateInpData());

        private string GenerateInpData()
            =>
                new InpDataGenerator(_config, _spssDataTransformer.VariableNames, OutDatFile.FileName)
                    .GenerateInpData();

        internal class SpssDataTransformer
        {
            private static readonly Regex NonNumerical = new Regex(@"[^\d,.-]+", RegexOptions.Compiled);
            private readonly Config _config;
            private readonly string[] _data;
            private int _amountOfColumns;

            public SpssDataTransformer(Config config, string[] data)
            {
                _config = config;
                _data = data;
            }

            public List<string> VariableNames { get; private set; } = new List<string>();

            public void TransformData()
            {
                foreach (var lInfo in _data.Select((x, i) => Tuple.Create(i, x)))
                    _data[lInfo.Item1] = TransformLine(lInfo);
            }

            private string TransformLine(Tuple<int, string> line)
            {
                var columns = line.Item2.Split(_config.ColumnSplitter);
                VerifyAmountOfColumns(line, columns);
                return columns.Any(x => NonNumerical.IsMatch(x))
                    ? ProcessVariableNamesLine(line, columns)
                    : ProcessValueLine(columns);
            }

            private void VerifyAmountOfColumns(Tuple<int, string> line, string[] columns)
            {
                if (line.Item1 == 0)
                    _amountOfColumns = columns.Length;
                else if (columns.Length != _amountOfColumns)
                    throw new Exception(
                        $"{HumanReadableLineNumber(line.Item1)} has {columns.Length} columns but should be {_amountOfColumns}");
            }

            private string ProcessVariableNamesLine(Tuple<int, string> line, string[] columns)
            {
                if (line.Item1 != 0)
                {
                    if (_config.IgnoreNonNumerical)
                        return ProcessValueLine(columns);
                    var info = GetContextInfo(line, columns);
                    throw new NonNumericalException(
                        $"There are non numerical characters on another line than the first. {HumanReadableLineNumber(line.Item1)}",
                        info);
                }
                VariableNames = columns.ToList();
                return null;
            }

            private static ContextInfo GetContextInfo(Tuple<int, string> line, string[] columns)
            {
                var firstMatch = columns.First(x => NonNumerical.IsMatch(x));
                return new ContextInfo
                {
                    FirstMatch = firstMatch,
                    Column = columns.ToList().IndexOf(firstMatch) + 1,
                    LineNumber = line.Item1,
                    Context = line.Item2
                };
            }

            private string ProcessValueLine(string[] columns)
            {
                foreach (var entry in columns.Select((x, i) => Tuple.Create(i, x)))
                    columns[entry.Item1] = ProcessLine(entry);
                return string.Join(_config.ColumnJoiner, columns);
            }

            private string ProcessLine(Tuple<int, string> column)
            {
                var r = column.Item2;
                r = ReplaceReplacements(r);
                r = ReplaceEmpty(r);
                return r;
            }

            private string ReplaceEmpty(string column)
            {
                if (_config.HasEmptyReplacement() && string.IsNullOrWhiteSpace(column))
                    return _config.EmptyReplacement;
                return column;
            }

            private string ReplaceReplacements(string column)
            {
                if (_config.Replacements == null) return column;
                return _config.Replacements.Aggregate(column,
                    (current, replacement) => current.Replace(replacement.Key, replacement.Value));
            }

            private static string HumanReadableLineNumber(int arrayIndex) => $"Line: {arrayIndex + 1}";
        }

        public class ContextInfo
        {
            public string FirstMatch { get; set; }
            public int Column { get; set; }
            public int LineNumber { get; set; }
            public string Context { get; set; }
        }

        internal class InpDataGenerator
        {
            private readonly Config _config;
            private readonly string _outDatFile;
            private readonly List<string> _variableNames;

            public InpDataGenerator(Config config, List<string> variableNames, string outDatFile)
            {
                _config = config;
                _variableNames = variableNames;
                _outDatFile = outDatFile;
            }

            public string GenerateInpData()
            {
                var sb = new StringBuilder();

                AddTooLongVariableNamesInfo(sb);
                AddNonUniqueVariableNamesInfo(sb);
                AddDataInfo(sb);
                AddVariablesInfo(sb);
                AddAnalysisInfo(sb);

                return sb.ToString();
            }

            private void AddTooLongVariableNamesInfo(StringBuilder sb)
            {
                var variables = GetTooLongVariableNames().ToArray();
                if (!variables.Any()) return;
                sb.AppendLine(
                    $"!\tThe following variable names are too long, you should make them shorter:{_config.NewLine}{SplitAndJoinVariablesForComment(variables)}");
                sb.AppendLine();
            }

            private IEnumerable<string> GetTooLongVariableNames()
                => _variableNames.Where(x => x.Length > _config.MaxHeaderLength);

            private void AddNonUniqueVariableNamesInfo(StringBuilder sb)
            {
                var variables = GetNonUniqueVariableNames().ToArray();
                if (!variables.Any()) return;
                sb.AppendLine(
                    $"!\tThe following variable names are not unique:{_config.NewLine}{SplitAndJoinVariablesForComment(variables)}");
                sb.AppendLine();
            }

            private IEnumerable<string> GetNonUniqueVariableNames()
                => _variableNames.GroupBy(x => x.ToLower()).Where(x => x.Count() > 1).Select(x => x.First());

            private string SplitAndJoinVariablesForComment(string[] variables)
                =>
                    SplitWhenLonger(JoinVariableNamesForComment(variables), $"!{_config.DefaultIndent}",
                        _config.MaxLineLength);

            private static string JoinVariableNamesForComment(string[] variables) => string.Join(", ", variables);

            private void AddDataInfo(StringBuilder sb)
            {
                sb.AppendLine("DATA:");
                sb.AppendLine($"{_config.DefaultIndent}FILE IS {_outDatFile};");
                sb.AppendLine();
            }

            private void AddVariablesInfo(StringBuilder sb)
            {
                if (!_variableNames.Any() && !_config.HasEmptyReplacement())
                    return;

                sb.AppendLine("VARIABLE:");
                if (_variableNames.Any())
                {
                    sb.AppendLine($"{_config.DefaultIndent}NAMES ARE");
                    sb.AppendLine(
                        $"{SplitWhenLonger(JoinVariableNames(), $"{_config.DefaultIndent}\t", _config.MaxLineLength)};");
                    sb.AppendLine($"{_config.DefaultIndent}IDVARIABLE IS {_variableNames.First()};");
                }

                if (_config.HasEmptyReplacement())
                    sb.AppendLine($"{_config.DefaultIndent}MISSING ARE ALL ({_config.EmptyReplacement});");

                sb.AppendLine();
            }

            private string JoinVariableNames() => string.Join(" ", _variableNames);

            private string SplitWhenLonger(string input, string prefix = "", int length = 80)
                =>
                    string.Join(_config.NewLine,
                        SplitWhenLongerInternal(input, length).Select(x => prefix + x));

            private static IEnumerable<string> SplitWhenLongerInternal(string input, int length = 80)
                => Regex.Split(input, @"(.{1," + length + @"})(?:\s|$)")
                    .Where(x => x.Length > 0);

            private void AddAnalysisInfo(StringBuilder sb)
            {
                sb.AppendLine("ANALYSIS:");
                sb.AppendLine($"{_config.DefaultIndent}TYPE IS {_config.AnalysisType};");
                sb.AppendLine();
            }
        }

        public class Config
        {
            public IDictionary<string, string> Replacements { get; set; } = new Dictionary<string, string> {{".", ","}};
            public string EmptyReplacement { get; set; } = "-999";
            public int MaxHeaderLength { get; set; } = 8;
            public int MaxLineLength { get; set; } = 80;
            public string AnalysisType { get; set; } = "BASIC";
            public string NewLine { get; set; } = Environment.NewLine;
            public string DefaultIndent { get; set; } = "\t\t";
            public string ColumnJoiner { get; set; } = "\t";
            public char ColumnSplitter { get; set; } = '\t';
            public bool IgnoreNonNumerical { get; set; }

            public bool HasEmptyReplacement() => EmptyReplacement != null;
        }
    }

    public class NonNumericalException : NotSupportedException
    {
        public NonNumericalException(string message, MplusConverter.ContextInfo context) : base(message)
        {
            Context = context;
        }

        public MplusConverter.ContextInfo Context { get; set; }
    }
}