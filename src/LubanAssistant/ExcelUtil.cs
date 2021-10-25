﻿using Luban.Common.Utils;
using Luban.Job.Cfg.DataExporters;
using Luban.Job.Cfg.Datas;
using Luban.Job.Cfg.DataSources.Excel;
using Luban.Job.Cfg.DataVisitors;
using Luban.Job.Cfg.Defs;
using Luban.Job.Cfg.Utils;
using Luban.Job.Common.Types;
using Microsoft.Office.Interop.Excel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LubanAssistant
{
    static class ExcelUtil
    {
        public static RawSheet ParseRawSheet(Worksheet sheet, Range toSaveRecordRows)
        {
            if (!ParseMetaAttrs(sheet, out var orientRow, out var titleRows, out var tableName))
            {
                throw new Exception($"meta行不合法");
            }

            if (!orientRow)
            {
                throw new Exception($"目前只支持行表");
            }

            Title title = ParseTitles(sheet);
            var cells = new List<List<Cell>>();

            var rangeAsArray = (object[,])toSaveRecordRows.Value;
            int lowBound0 = rangeAsArray.GetLowerBound(0);
            for (int r = lowBound0, n = r + rangeAsArray.GetLength(0); r < n; r++)
            {
                var rowCell = new List<Cell>();
                for (int i = title.FromIndex; i <= title.ToIndex; i++)
                {
                    rowCell.Add(new Cell(r - 1, i, rangeAsArray[r, i + 1]));
                }
                cells.Add(rowCell);
            }
            return new RawSheet() { Title = title, TitleRowCount = titleRows, TableName = tableName, Cells = cells };
        }

        public static RawSheet ParseRawSheetTitleOnly(Worksheet sheet)
        {
            if (!ParseMetaAttrs(sheet, out var orientRow, out var titleRows, out var tableName))
            {
                throw new Exception($"meta行不合法");
            }

            if (!orientRow)
            {
                throw new Exception($"目前只支持行表");
            }

            Title title = ParseTitles(sheet);
            var cells = new List<List<Cell>>();
            return new RawSheet() { Title = title, TitleRowCount = titleRows, TableName = tableName, Cells = cells };
        }

        public static bool ParseMetaAttrs(Worksheet sheet, out bool orientRow, out int titleRows, out string tableName)
        {
            Range metaRow = sheet.Rows[1];

            var cells = new List<string>();
            for (int i = 1, n = sheet.UsedRange.Columns.Count; i <= n; i++)
            {
                cells.Add(((Range)metaRow.Cells[1, i]).Value?.ToString());
            }
            return SheetLoadUtil.TryParseMeta(cells, out orientRow, out titleRows, out tableName);
        }

        public static Title ParseTitles(Worksheet sheet)
        {
            int titleRows = 1;
            Range c1 = sheet.Cells[2, 1];
            if (c1.MergeCells)
            {
                titleRows = c1.MergeArea.Count;
            }
            var rootTile = new Title()
            {
                FromIndex = 0,
                ToIndex = sheet.UsedRange.Columns.Count - 1,
                Name = "__root__",
                Root = true,
                Tags = new Dictionary<string, string>(),
            };
            ParseSubTitle(sheet, 2, titleRows + 1, rootTile);
            rootTile.ToIndex = rootTile.SubTitleList.Max(t => t.ToIndex);
            rootTile.Init();
            return rootTile;
        }

        private static void ParseSubTitle(Worksheet sheet, int rowIndex, int maxRowIndex, Title title)
        {
            Range row = sheet.Rows[rowIndex];
            for (int i = title.FromIndex; i <= title.ToIndex; i++)
            {
                Range subTitleRange = row.Cells[1, i + 1];
                string subTitleValue = subTitleRange.Value?.ToString();
                if (string.IsNullOrWhiteSpace(subTitleValue))
                {
                    continue;
                }

                var (subTitleName, tags) = SheetLoadUtil.ParseNameAndMetaAttrs(subTitleValue);


                var newSubTitle = new Title()
                {
                    Name = subTitleName,
                    FromIndex = i,
                    Tags = tags,
                };

                if (subTitleRange.MergeCells)
                {
                    newSubTitle.ToIndex = i + subTitleRange.MergeArea.Count - 1;
                }
                else
                {
                    newSubTitle.ToIndex = i;
                }
                title.AddSubTitle(newSubTitle);
            }
            if (rowIndex < maxRowIndex)
            {
                foreach (var subTitle in title.SubTitleList)
                {
                    ParseSubTitle(sheet, rowIndex + 1, maxRowIndex, subTitle);
                }
            }
        }

        public static void FillRecords(Worksheet sheet, int titleRowNum, Title title, TableDataInfo tableDataInfo)
        {
            int usedRowNum = sheet.UsedRange.Rows.Count;
            if (usedRowNum > titleRowNum + 1)
            {
                Range allDataRange = sheet.Range[sheet.Cells[titleRowNum + 2, 1], sheet.Cells[usedRowNum, sheet.UsedRange.Columns.Count]];
                allDataRange.ClearContents();
            }

            //int nextRowIndex = titleRowNum + 2;

            // 对于 int和long类型记录，按值排序
            var records = tableDataInfo.MainRecords;
            DefField keyField = tableDataInfo.Table.IndexField;
            if (keyField != null && (keyField.CType is TInt || keyField.CType is TLong))
            {
                string keyFieldName = keyField.Name;
                records.Sort((a, b) =>
                {
                    DType keya = a.Data.GetField(keyFieldName);
                    DType keyb = b.Data.GetField(keyFieldName);
                    switch (keya)
                    {
                        case DInt ai: return ai.Value.CompareTo((keyb as DInt).Value);
                        case DLong al: return al.Value.CompareTo((keyb as DLong).Value);
                        default: throw new NotSupportedException();
                    }
                });
            }

            int totalRowCount = 0;
            var dataRangeArray = new List<object[]>();
            foreach (var rec in records)
            {
                var fillVisitor = new FillSheetVisitor(dataRangeArray, title.ToIndex + 1, totalRowCount);
                totalRowCount += rec.Data.Apply(fillVisitor, title);
            }

            object[,] resultDataRangeArray = new object[dataRangeArray.Count, title.ToIndex + 1];
            for (int i = 0; i < dataRangeArray.Count; i++)
            {
                object[] row = dataRangeArray[i];
                for (int j = 0; j < row.Length; j++)
                {
                    resultDataRangeArray[i, j] = row[j];
                }
            }

            Range recordFillRange = sheet.Range[sheet.Cells[titleRowNum + 2, 1], sheet.Cells[titleRowNum + 1 + dataRangeArray.Count, title.ToIndex + 1]];
            recordFillRange.Value = resultDataRangeArray;
        }

        public static List<Record> LoadRecordsInRange(DefTable table, Worksheet sheet, Title title, Range toSaveRecordRows)
        {
            RawSheet rawSheet = ParseRawSheet(sheet, toSaveRecordRows);
            var excelSource = new ExcelDataSource();
            excelSource.Load(rawSheet);

            return excelSource.ReadMulti(table.ValueTType);
        }

        public static async Task SaveRecordsAsync(string inputDataDir, DefTable table, List<Record> records)
        {
            var recordOutputDir = Path.Combine(inputDataDir, table.InputFiles[0]);
            string index = table.IndexField.Name;

            var saveRecordTasks = new List<Task>();

            foreach (var r in records)
            {
                saveRecordTasks.Add(Task.Run(async () =>
                {
                    var ss = new MemoryStream();
                    var jsonWriter = new Utf8JsonWriter(ss, new JsonWriterOptions()
                    {
                        Indented = true,
                        SkipValidation = false,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
                    });
                    RawJsonExportor.Ins.Accept(r.Data, jsonWriter);

                    jsonWriter.Flush();
                    byte[] resultBytes = DataUtil.StreamToBytes(ss);
                    var key = r.Data.GetField(index);
                    var fileName = $"{key.Apply(ToStringVisitor.Ins)}.json";

                    // 只有文件内容改变才重新加载
                    string fileFullPath = Path.Combine(recordOutputDir, fileName);
                    if (File.Exists(fileFullPath))
                    {
                        var oldBytes = await FileUtil.ReadAllBytesAsync(fileFullPath);
                        if (System.Collections.StructuralComparisons.StructuralEqualityComparer.Equals(resultBytes, oldBytes))
                        {
                            return;
                        }
                    }
                    await FileUtil.SaveFileAsync(recordOutputDir, fileName, resultBytes);
                }));
            }
            await Task.WhenAll(saveRecordTasks);
        }

        //public static void FillRecord(Worksheet sheet, ref int nextRowIndex, Title title, Record record)
        //{

        //    nextRowIndex += FillField(sheet, nextRowIndex, title, record.Data);
        //}
    }
}