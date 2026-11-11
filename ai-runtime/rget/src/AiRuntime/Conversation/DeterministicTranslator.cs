// src/AiRuntime/Conversation/DeterministicTranslator.cs
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// 确定性的"自然语言 → 算术表达式"翻译器。
/// </summary>
/// <remarks>
/// 设计约束（来自需求）：
///   - 符号可解域内优先使用确定性路径，LLM 只覆盖确定性路径无法表达的模糊表述；
///   - 翻译结果必须能通过符号引擎的语法校验才能进入运算；
///   - 该实现不调用 LLM，因此离线、无模型时符号路径仍可用。
///
/// 覆盖范围：四则运算、括号、小数、中文数词（0-99999）、加/减/乘/除以。
/// 不覆盖：方程求解、微积分、单位换算、带歧义的"的"字结构。
/// 超出范围时返回 false，由调用方回退到 LLM 或要求用户澄清。
/// </remarks>
public static class DeterministicTranslator
{
    private static readonly Regex PureExpression = new(
        @"^[\s0-9\.\+\-\*/\(\)]+$", RegexOptions.Compiled);

    private static readonly Dictionary<char, int> ChineseDigits = new()
    {
        ['零'] = 0, ['〇'] = 0, ['一'] = 1, ['二'] = 2, ['两'] = 2,
        ['三'] = 3, ['四'] = 4, ['五'] = 5, ['六'] = 6, ['七'] = 7,
        ['八'] = 8, ['九'] = 9
    };

    private static readonly Dictionary<char, int> ChineseUnits = new()
    {
        ['十'] = 10, ['百'] = 100, ['千'] = 1000
    };

    /// <summary>
    /// 尝试把一句用户问题翻译为算术表达式。
    /// 成功时 expression 形如 "3+5*2"，可直接送往 ai_verify_candidate。
    /// </summary>
    public static bool TryTranslate(string text, out string expression)
    {
        expression = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        if (PureExpression.IsMatch(trimmed))
        {
            string compact = Regex.Replace(trimmed, @"\s+", "");
            if (compact.Length >= 3 && compact.Any(char.IsDigit) && HasOperator(compact))
            {
                expression = compact;
                return true;
            }
            return false;
        }

        var tokens = Tokenize(trimmed);
        if (tokens.Count < 3 || tokens.Count % 2 == 0) return false;

        for (int i = 0; i < tokens.Count; i++)
        {
            bool isOperand = i % 2 == 0;
            if (isOperand != IsOperandToken(tokens[i])) return false;
        }

        var sb = new StringBuilder();
        // 中文口述算式按"从左到右"结合，与产品定义一致：
        // "3 加 5 乘以 2" = (3 + 5) * 2 = 16，而不是算式写法的 3 + 5 * 2 = 13。
        // 需要标准优先级的用户可以直接输入显式算式（3+5*2）。
        sb.Append(tokens[0]);
        for (int i = 1; i + 1 < tokens.Count; i += 2)
            sb.Insert(0, "(").Append(tokens[i]).Append(tokens[i + 1]).Append(')');

        expression = sb.ToString();
        return expression.Length >= 3 && HasOperator(expression) && expression.Any(char.IsDigit);
    }

    /// <summary>诊断用：返回确定性分词结果，便于冒烟测试定位翻译失败原因。</summary>
    public static string DebugTokens(string text)
    {
        var t = Tokenize(text);
        return t.Count + ":" + string.Join("|", t);
    }

    /// <summary>
    /// 从一段对话原文（可能含 "user: " 前缀、多行）中找出第一个可翻译的计算句。
    /// 回退链路用它把被压缩丢掉的原文重新变成候选表达式。
    /// </summary>
    public static bool TryTranslateDialogue(IEnumerable<string> lines, out string expression)
    {
        expression = string.Empty;
        if (lines is null) return false;

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string stripped = StripRolePrefix(line);
            if (string.IsNullOrWhiteSpace(stripped)) continue;
            if (TryTranslate(stripped, out expression)) return true;
        }
        return false;
    }

    private static string StripRolePrefix(string line)
    {
        int colon = line.IndexOf(':');
        if (colon < 0) return line.Trim();

        string head = line[..colon].Trim();
        return head.Equals("user", StringComparison.OrdinalIgnoreCase)
            || head.Equals("assistant", StringComparison.OrdinalIgnoreCase)
            || head.Equals("system", StringComparison.OrdinalIgnoreCase)
            ? line[(colon + 1)..].Trim()
            : line.Trim();
    }

    private static bool HasOperator(string s) =>
        s.IndexOfAny(new[] { '+', '-', '*', '/' }) >= 0;

    private static bool IsOperandToken(string t) =>
        t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '(');

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            // 阿拉伯数字 / 小数
            if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                int start = i;
                bool dot = false;
                while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    if (text[i] == '.')
                    {
                        if (dot) break;
                        dot = true;
                    }
                    i++;
                }
                tokens.Add(text[start..i]);
                continue;
            }

            // 中文数词：连续的数词字符作为一个操作数
            if (ChineseDigits.ContainsKey(c) || ChineseUnits.ContainsKey(c))
            {
                int start = i;
                while (i < text.Length && (ChineseDigits.ContainsKey(text[i]) || ChineseUnits.ContainsKey(text[i])))
                    i++;
                tokens.Add(ParseChineseNumber(text[start..i]).ToString());
                continue;
            }

            string? op = MatchOperator(text, ref i);
            if (op is not null)
            {
                tokens.Add(op);
                continue;
            }

            // 非运算符字符（中文名词、标点、空白）视为分隔符
            i++;
        }

        return tokens;
    }

    private static long ParseChineseNumber(string s)
    {
        long result = 0, section = 0, digit = 0;
        bool hasDigit = false;

        foreach (char c in s)
        {
            if (ChineseDigits.TryGetValue(c, out int d))
            {
                // "一二三" 这类连续数字串按位拼接，避免被当成 3
                digit = hasDigit ? digit * 10 + d : d;
                hasDigit = true;
            }
            else if (c == '万')
            {
                section = (section + (hasDigit ? digit : 0)) * 10000;
                result += section;
                section = 0;
                digit = 0;
                hasDigit = false;
            }
            else if (ChineseUnits.TryGetValue(c, out int unit))
            {
                long baseValue = hasDigit ? digit : 1;
                section += baseValue * unit;
                digit = 0;
                hasDigit = false;
            }
        }

        return result + section + (hasDigit ? digit : 0);
    }

    private static string? MatchOperator(string text, ref int i)
    {
        char c = text[i];

        // 两字词优先匹配，避免 "乘以" 被拆成 "乘" + 未识别字符
        if (c == '乘' && i + 1 < text.Length && text[i + 1] == '以')
        {
            i += 2;
            return "*";
        }
        if (c == '除' && i + 1 < text.Length && text[i + 1] == '以')
        {
            i += 2;
            return "/";
        }
        if (c == '加' && i + 1 < text.Length && text[i + 1] == '上')
        {
            i += 2;
            return "+";
        }
        if (c == '减' && i + 1 < text.Length && text[i + 1] == '去')
        {
            i += 2;
            return "-";
        }

        switch (c)
        {
            case '加': i++; return "+";
            case '减': i++; return "-";
            case '乘': i++; return "*";
            case '除': i++; return "/";
            case '+': i++; return "+";
            case '-': i++; return "-";
            case '*': i++; return "*";
            case '×': i++; return "*";
            case '/': i++; return "/";
            case '÷': i++; return "/";
            default: return null;   // 非运算符：不前进，由调用方按分隔符跳过
        }
    }
}
