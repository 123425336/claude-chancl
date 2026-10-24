using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ClaudeCL.Core;

/// <summary>
/// 日志自动限频：把短时间内大量重复的日志折叠成一条提示。
/// 1. 连续循环：同一段（1~8 行）反复出现，例如栈溢出时的堆栈，显示两遍后折叠；
/// 2. 高频刷屏：同一行在一秒内出现太多次（即使中间夹着别的日志），超出部分折叠。
/// 比较时忽略行首时间戳。不是线程安全的，调用方需要加锁。
/// </summary>
public sealed class LogFolder
{
    private const int MaxPeriod = 8;
    /// <summary>同一行每秒最多显示的次数</summary>
    private const int RatePerSecond = 8;
    /// <summary>循环持续时，每隔多久汇报一次进度</summary>
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(3);

    // 去掉行首的时间戳，例如 [12:00:01] [12:00:01.123] [24Oct2026 12:00:01.123] 12:00:01.123
    private static readonly Regex TimePrefix = new(
        @"^\s*(?:\[[^\]]*\d{1,2}:\d{2}:\d{2}[^\]]*\]\s*|\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?\s*)+", RegexOptions.Compiled);

    // ---------------- 连续循环 ----------------
    private readonly List<string> _history = new();
    private List<string>? _pattern;
    private int _pos;
    private long _cycleFolded;
    private long _cycleReported;
    private DateTime _lastProgress;

    // ---------------- 高频刷屏 ----------------
    private DateTime _bucketStart = DateTime.MinValue;
    private readonly Dictionary<string, int> _bucket = new();
    private readonly Dictionary<string, (int Count, string Sample)> _suppressed = new();

    public static string Key(string line) => TimePrefix.Replace(line, "").TrimEnd();

    private static string Notice(string text) => $"[ClaudeCL] …… {text}";

    /// <summary>
    /// 送入一行日志，返回应当显示的内容（可能为空，也可能附带折叠提示）
    /// </summary>
    public List<string> Feed(string line, DateTime now)
    {
        var output = new List<string>();
        RollBucket(now, output);
        var key = Key(line);

        // 正在循环中：继续匹配就折叠，不匹配就结束循环
        if (_pattern is not null)
        {
            if (key == _pattern[_pos])
            {
                _cycleFolded++;
                _pos = (_pos + 1) % _pattern.Count;
                ReportProgress(now, output);
                return output;
            }
            EndCycle(output);
        }

        // 高频刷屏的限制
        var count = _bucket.TryGetValue(key, out var c) ? c + 1 : 1;
        _bucket[key] = count;
        if (count > RatePerSecond)
        {
            (int Count, string Sample) s = _suppressed.TryGetValue(key, out var old) ? old : (0, line);
            _suppressed[key] = (s.Count + 1, s.Sample);
            return output;
        }

        output.Add(line);
        _history.Add(key);
        if (_history.Count > MaxPeriod * 2) _history.RemoveAt(0);
        DetectCycle(now);
        return output;
    }

    /// <summary>
    /// 定时调用：刷屏停止后补上折叠提示，长时间循环时汇报进度
    /// </summary>
    public List<string> Tick(DateTime now)
    {
        var output = new List<string>();
        RollBucket(now, output);
        if (_pattern is not null) ReportProgress(now, output);
        return output;
    }

    /// <summary>
    /// 游戏结束时调用，输出所有还没汇报的折叠提示
    /// </summary>
    public List<string> FlushAll()
    {
        var output = new List<string>();
        if (_pattern is not null) EndCycle(output);
        EmitSuppressed(output);
        _bucket.Clear();
        return output;
    }

    private void DetectCycle(DateTime now)
    {
        // 最近的 2p 行由同一段 p 行重复两次组成，就认为进入了循环（优先找最短的循环）
        for (var p = 1; p <= MaxPeriod && p * 2 <= _history.Count; p++)
        {
            var n = _history.Count;
            var match = true;
            for (var i = 0; i < p && match; i++)
                match = _history[n - 1 - i] == _history[n - 1 - p - i];
            // 空行连续出现很常见，不当作循环
            if (!match || p == 1 && _history[^1].Length == 0) continue;
            _pattern = _history.Skip(n - p).ToList();
            _pos = 0;
            _cycleFolded = 0;
            _cycleReported = 0;
            _lastProgress = now;
            return;
        }
    }

    private void ReportProgress(DateTime now, List<string> output)
    {
        if (_cycleFolded == _cycleReported || now - _lastProgress < ProgressInterval) return;
        _lastProgress = now;
        _cycleReported = _cycleFolded;
        output.Add(Notice($"仍在重复：上面 {_pattern!.Count} 行已经折叠了 {_cycleFolded:N0} 行"));
    }

    private void EndCycle(List<string> output)
    {
        if (_cycleFolded > 0)
        {
            var p = _pattern!.Count;
            output.Add(p == 1
                ? Notice($"上面这行又重复了 {_cycleFolded:N0} 次，已自动折叠")
                : Notice($"上面 {p} 行又循环重复了 {_cycleFolded / p:N0} 次（共 {_cycleFolded:N0} 行），已自动折叠"));
        }
        _pattern = null;
        _history.Clear();
    }

    private void RollBucket(DateTime now, List<string> output)
    {
        if (now - _bucketStart < TimeSpan.FromSeconds(1)) return;
        EmitSuppressed(output);
        _bucket.Clear();
        _bucketStart = now;
    }

    private void EmitSuppressed(List<string> output)
    {
        foreach (var (_, (count, sample)) in _suppressed)
        {
            var text = Key(sample);
            if (text.Length > 120) text = text[..120] + "…";
            output.Add(Notice($"这行在 1 秒内又出现了 {count:N0} 次，已自动折叠：{text}"));
        }
        _suppressed.Clear();
    }
}
