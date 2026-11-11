/* generated/sympy_engine.cpp
 *
 * 符号引擎（C++ 实现）：
 *   - 任意数值中缀表达式（+ - * / ^、括号、常见函数）
 *   - 区间运算：[a,b] 闭区间、(a,b) 开区间、混合开闭，四则运算
 *   - 单变量 x 的一次/二次等式与不等式求解（不含超越方程）
 *
 * 对外仍保持 sympy_engine.h 的 C ABI：sympy_validate / sympy_execute /
 * sympy_free_string。
 */

#include "sympy_engine.h"

#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr double kEps = 1e-12;

struct Poly
{
    double c0 = 0.0, c1 = 0.0, c2 = 0.0;   // c0 + c1*x + c2*x^2
};

struct Interval
{
    double lo = -std::numeric_limits<double>::infinity();
    double hi = std::numeric_limits<double>::infinity();
    bool loOpen = true;
    bool hiOpen = true;
};

enum class Kind { Number, Interval, Polynomial };

struct Val
{
    Kind kind = Kind::Number;
    double number = 0.0;
    Interval interval;
    Poly poly;
};

Val makeNumber(double v) { Val x; x.kind = Kind::Number; x.number = v; return x; }
Val makeInterval(const Interval& i) { Val x; x.kind = Kind::Interval; x.interval = i; return x; }
Val makePoly(const Poly& p) { Val x; x.kind = Kind::Polynomial; x.poly = p; return x; }

bool isZero(double v) { return std::fabs(v) < kEps; }

std::string formatNumber(double v)
{
    if (isZero(v)) return "0";

    char buf[64];
    std::snprintf(buf, sizeof(buf), "%.12g", v);
    return buf;
}

std::string formatInfinity(double v, bool upper)
{
    if (std::isinf(v)) return upper ? "+∞" : "-∞";
    return formatNumber(v);
}

std::string formatInterval(const Interval& i)
{
    std::string out;
    out += i.loOpen ? "(" : "[";
    out += formatInfinity(i.lo, false);
    out += ", ";
    out += formatInfinity(i.hi, true);
    out += i.hiOpen ? ")" : "]";
    return out;
}

Poly polyAdd(const Poly& a, const Poly& b)
{
    return Poly{a.c0 + b.c0, a.c1 + b.c1, a.c2 + b.c2};
}

Poly polySub(const Poly& a, const Poly& b)
{
    return Poly{a.c0 - b.c0, a.c1 - b.c1, a.c2 - b.c2};
}

Poly polyNeg(const Poly& a)
{
    return Poly{-a.c0, -a.c1, -a.c2};
}

Poly polyMul(const Poly& a, const Poly& b)
{
    // 覆盖到二次；更高次直接由调用方拒绝
    Poly r;
    r.c0 = a.c0 * b.c0;
    r.c1 = a.c0 * b.c1 + a.c1 * b.c0;
    r.c2 = a.c0 * b.c2 + a.c1 * b.c1 + a.c2 * b.c0;
    return r;
}

bool polyIsConstant(const Poly& p) { return isZero(p.c1) && isZero(p.c2); }

// ---------------------------------------------------------------------------
// 解析器
// ---------------------------------------------------------------------------

class Parser
{
public:
    explicit Parser(std::string_view text) : s_(text) {}

    Val parseExpression()
    {
        Val v = parseTerm();
        for (;;)
        {
            skipSpaces();
            if (match('+')) v = add(v, parseTerm());
            else if (match('-')) v = sub(v, parseTerm());
            else return v;
        }
    }

    void ensureEnd()
    {
        skipSpaces();
        if (pos_ != s_.size()) throw std::runtime_error("unexpected token");
    }

private:
    std::string_view s_;
    std::size_t pos_ = 0;

    void skipSpaces()
    {
        while (pos_ < s_.size() && std::isspace(static_cast<unsigned char>(s_[pos_]))) ++pos_;
    }

    bool match(char c)
    {
        skipSpaces();
        if (pos_ < s_.size() && s_[pos_] == c) { ++pos_; return true; }
        return false;
    }

    char peek()
    {
        skipSpaces();
        return pos_ < s_.size() ? s_[pos_] : '\0';
    }

    Val parseTerm()
    {
        Val v = parseUnary();
        for (;;)
        {
            skipSpaces();
            if (match('*')) { v = mul(v, parseUnary()); continue; }
            if (match('/')) { v = div(v, parseUnary()); continue; }

            // 隐式乘法：2x、2(、x(、)( 等
            char c = peek();
            if (c == 'x' || c == '(' || c == '[' ||
                std::isalpha(static_cast<unsigned char>(c)))
            {
                v = mul(v, parseUnary());
                continue;
            }
            return v;
        }
    }

    Val parseUnary()
    {
        skipSpaces();
        if (match('+')) return parseUnary();
        if (match('-')) return neg(parseUnary());
        return parsePower();
    }

    Val parsePower()
    {
        Val base = parsePrimary();
        skipSpaces();
        if (match('^'))
        {
            Val exp = parseUnary();   // 支持 2^-2；幂仍由右侧 parsePower 保持右结合
            return power(base, exp);
        }
        return base;
    }

    Val parsePrimary()
    {
        skipSpaces();
        if (pos_ >= s_.size()) throw std::runtime_error("unexpected end");

        char c = s_[pos_];

        if (c == '(' || c == '[')
        {
            if (isIntervalStart())
                return parseInterval();
            if (c == '[') throw std::runtime_error("invalid interval");
            return parseParenthesized();
        }

        if (std::isdigit(static_cast<unsigned char>(c)) || c == '.')
            return parseNumber();

        if (std::isalpha(static_cast<unsigned char>(c)) || c == '_')
            return parseIdentifier();

        throw std::runtime_error("unexpected character");
    }

    bool isIntervalStart()
    {
        // '(' 后有顶层逗号才视为区间；'[' 一律按区间处理
        std::size_t i = pos_;
        if (s_[i] == '[') return true;
        int depth = 0;
        bool comma = false;

        for (; i < s_.size(); ++i)
        {
            char c = s_[i];
            if (c == '(' || c == '[') { ++depth; continue; }
            if (c == ')' || c == ']')
            {
                --depth;
                if (depth == 0) return comma;
                continue;
            }
            if (c == ',' && depth == 1) comma = true;
        }
        return false;
    }

    Val parseParenthesized()
    {
        ++pos_;   // (
        Val v = parseExpression();
        if (!match(')')) throw std::runtime_error("missing )");
        return v;
    }

    Val parseInterval()
    {
        bool loOpen = s_[pos_] == '(';
        ++pos_;

        Val lo = parseExpression();
        if (lo.kind != Kind::Number) throw std::runtime_error("interval endpoint must be numeric");
        if (!match(',')) throw std::runtime_error("interval needs comma");

        Val hi = parseExpression();
        if (hi.kind != Kind::Number) throw std::runtime_error("interval endpoint must be numeric");

        skipSpaces();
        if (pos_ >= s_.size() || (s_[pos_] != ')' && s_[pos_] != ']'))
            throw std::runtime_error("missing interval close");
        bool hiOpen = s_[pos_] == ')';
        ++pos_;

        if (lo.number > hi.number) throw std::runtime_error("interval lower bound > upper bound");

        Interval i;
        i.lo = lo.number;
        i.hi = hi.number;
        i.loOpen = loOpen;
        i.hiOpen = hiOpen;
        return makeInterval(i);
    }

    Val parseNumber()
    {
        std::string rest(s_.substr(pos_));
        char* end = nullptr;
        double v = std::strtod(rest.c_str(), &end);
        if (end == nullptr || end == rest.c_str()) throw std::runtime_error("invalid number");

        pos_ += static_cast<std::size_t>(end - rest.c_str());
        return makeNumber(v);
    }

    Val parseIdentifier()
    {
        std::size_t start = pos_;
        while (pos_ < s_.size())
        {
            char c = s_[pos_];
            if (!(std::isalnum(static_cast<unsigned char>(c)) || c == '_')) break;
            ++pos_;
        }
        std::string name(s_.substr(start, pos_ - start));

        if (name == "x") return makePoly(Poly{0, 1, 0});
        if (name == "pi") return makeNumber(3.14159265358979323846);
        if (name == "e") return makeNumber(2.71828182845904523536);

        if (!match('(')) throw std::runtime_error("unknown identifier: " + name);

        std::vector<Val> args;
        skipSpaces();
        if (peek() == ')') { ++pos_; }
        else
        {
            for (;;)
            {
                args.push_back(parseExpression());
                if (match(',')) continue;
                if (match(')')) break;
                throw std::runtime_error("missing ) in function call");
            }
        }

        return callFunction(name, args);
    }

    Val callFunction(const std::string& name, const std::vector<Val>& rawArgs)
    {
        for (const auto& a : rawArgs)
        {
            if (a.kind == Kind::Polynomial)
                throw std::runtime_error("functions with variable x are unsupported");
        }

        std::vector<double> args;
        for (const auto& a : rawArgs)
        {
            if (a.kind != Kind::Number) throw std::runtime_error("function argument must be numeric");
            args.push_back(a.number);
        }

        auto require = [&](std::size_t n)
        {
            if (args.size() != n) throw std::runtime_error("wrong argument count for " + name);
        };

        if (name == "sqrt") { require(1); if (args[0] < 0) throw std::runtime_error("sqrt domain"); return makeNumber(std::sqrt(args[0])); }
        if (name == "abs") { require(1); return makeNumber(std::fabs(args[0])); }
        if (name == "exp") { require(1); return makeNumber(std::exp(args[0])); }
        if (name == "log") { require(1); if (args[0] <= 0) throw std::runtime_error("log domain"); return makeNumber(std::log(args[0])); }
        if (name == "sin") { require(1); return makeNumber(std::sin(args[0])); }
        if (name == "cos") { require(1); return makeNumber(std::cos(args[0])); }
        if (name == "tan") { require(1); return makeNumber(std::tan(args[0])); }
        if (name == "pow") { require(2); return makeNumber(std::pow(args[0], args[1])); }
        if (name == "min") { require(2); return makeNumber(std::min(args[0], args[1])); }
        if (name == "max") { require(2); return makeNumber(std::max(args[0], args[1])); }
        if (name == "pythagorean") { require(2); return makeNumber(std::sqrt(args[0]*args[0] + args[1]*args[1])); }
        if (name == "quadratic_root")
        {
            require(2);
            double disc = args[1]*args[1] - 4*args[0];
            if (disc < 0) return makeNumber(std::nan(""));
            return makeNumber((-args[1] + std::sqrt(disc)) / 2);
        }

        throw std::runtime_error("unknown function: " + name);
    }

    static Interval toInterval(const Val& v)
    {
        if (v.kind == Kind::Number)
        {
            Interval i; i.lo = i.hi = v.number; i.loOpen = i.hiOpen = false; return i;
        }
        if (v.kind == Kind::Interval) return v.interval;
        throw std::runtime_error("variable x in interval operation");
    }

    static Val add(const Val& a, const Val& b)
    {
        if (a.kind == Kind::Polynomial || b.kind == Kind::Polynomial)
            return makePoly(polyAdd(toPoly(a), toPoly(b)));
        if (a.kind == Kind::Interval || b.kind == Kind::Interval)
        {
            Interval x = toInterval(a), y = toInterval(b); Interval r;
            r.lo = x.lo + y.lo; r.hi = x.hi + y.hi;
            r.loOpen = x.loOpen || y.loOpen; r.hiOpen = x.hiOpen || y.hiOpen;
            return makeInterval(r);
        }
        return makeNumber(a.number + b.number);
    }

    static Val sub(const Val& a, const Val& b)
    {
        if (a.kind == Kind::Polynomial || b.kind == Kind::Polynomial)
            return makePoly(polySub(toPoly(a), toPoly(b)));
        if (a.kind == Kind::Interval || b.kind == Kind::Interval)
        {
            Interval x = toInterval(a), y = toInterval(b); Interval r;
            r.lo = x.lo - y.hi; r.hi = x.hi - y.lo;
            r.loOpen = x.loOpen || y.hiOpen; r.hiOpen = x.hiOpen || y.loOpen;
            return makeInterval(r);
        }
        return makeNumber(a.number - b.number);
    }

    static Val mul(const Val& a, const Val& b)
    {
        if (a.kind == Kind::Polynomial || b.kind == Kind::Polynomial)
        {
            Poly pa = toPoly(a), pb = toPoly(b);
            if (pa.c2 != 0 && pb.c1 != 0) throw std::runtime_error("degree > 2 unsupported");
            if (pa.c1 != 0 && pb.c2 != 0) throw std::runtime_error("degree > 2 unsupported");
            if (pa.c2 != 0 && pb.c2 != 0) throw std::runtime_error("degree > 2 unsupported");
            return makePoly(polyMul(pa, pb));
        }
        if (a.kind == Kind::Interval || b.kind == Kind::Interval)
        {
            Interval x = toInterval(a), y = toInterval(b);
            double p1 = x.lo * y.lo, p2 = x.lo * y.hi, p3 = x.hi * y.lo, p4 = x.hi * y.hi;
            Interval r;
            r.lo = std::min(std::min(p1, p2), std::min(p3, p4));
            r.hi = std::max(std::max(p1, p2), std::max(p3, p4));
            r.loOpen = x.loOpen || x.hiOpen || y.loOpen || y.hiOpen;
            r.hiOpen = r.loOpen;
            return makeInterval(r);
        }
        return makeNumber(a.number * b.number);
    }

    static Val div(const Val& a, const Val& b)
    {
        if (b.kind == Kind::Polynomial)
        {
            Poly pb = b.poly;
            if (!polyIsConstant(pb)) throw std::runtime_error("division by variable unsupported");
            return div(a, makeNumber(pb.c0));
        }
        if (b.kind == Kind::Number)
        {
            if (isZero(b.number)) throw std::runtime_error("division by zero");
            if (a.kind == Kind::Polynomial) return makePoly(Poly{a.poly.c0/b.number, a.poly.c1/b.number, a.poly.c2/b.number});
            if (a.kind == Kind::Interval)
            {
                Interval r = a.interval; r.lo /= b.number; r.hi /= b.number;
                if (r.lo > r.hi) std::swap(r.lo, r.hi);
                return makeInterval(r);
            }
            return makeNumber(a.number / b.number);
        }

        // 区间除法
        Interval y = toInterval(b);
        if (y.lo < 0 && y.hi > 0) throw std::runtime_error("division by interval containing zero");
        if (isZero(y.lo) || isZero(y.hi)) throw std::runtime_error("division by interval containing zero");

        Interval recip;
        recip.lo = 1.0 / y.hi; recip.hi = 1.0 / y.lo;
        recip.loOpen = y.hiOpen; recip.hiOpen = y.loOpen;
        if (recip.lo > recip.hi) std::swap(recip.lo, recip.hi);
        return mul(a, makeInterval(recip));
    }

    static Val neg(const Val& a)
    {
        if (a.kind == Kind::Polynomial) return makePoly(polyNeg(a.poly));
        if (a.kind == Kind::Interval)
        {
            Interval r; r.lo = -a.interval.hi; r.hi = -a.interval.lo;
            r.loOpen = a.interval.hiOpen; r.hiOpen = a.interval.loOpen;
            return makeInterval(r);
        }
        return makeNumber(-a.number);
    }

    static Val power(const Val& base, const Val& exp)
    {
        if (exp.kind != Kind::Number) throw std::runtime_error("power exponent must be numeric");
        double e = exp.number;

        if (base.kind == Kind::Polynomial)
        {
            Poly p = toPoly(base);
            if (std::fabs(e - 0) < kEps) return makePoly(Poly{1,0,0});
            if (std::fabs(e - 1) < kEps) return base;
            if (std::fabs(e - 2) < kEps) return makePoly(polyMul(p, p));
            throw std::runtime_error("only x^0..2 supported");
        }

        if (base.kind == Kind::Interval)
        {
            int n = static_cast<int>(std::llround(e));
            if (std::fabs(e - n) > kEps || n < 0 || n > 4)
                throw std::runtime_error("interval power supports integer 0..4");
            Val r = makeNumber(1);
            for (int i = 0; i < n; ++i) r = mul(r, base);
            return r;
        }

        return makeNumber(std::pow(base.number, e));
    }

    static Poly toPoly(const Val& v)
    {
        if (v.kind == Kind::Polynomial) return v.poly;
        if (v.kind == Kind::Number) return Poly{v.number, 0, 0};
        throw std::runtime_error("interval in polynomial expression");
    }
};

// ---------------------------------------------------------------------------
// 比较与求解
// ---------------------------------------------------------------------------

struct Comparison
{
    std::string left;
    std::string op;
    std::string right;
};

bool splitComparison(const std::string& s, Comparison& out)
{
    int depth = 0;
    int found = 0;

    for (std::size_t i = 0; i < s.size(); ++i)
    {
        char c = s[i];
        if (c == '(' || c == '[') { ++depth; continue; }
        if (c == ')' || c == ']') { --depth; continue; }
        if (depth != 0) continue;

        std::size_t len = 0;
        std::string op;

        if (c == '<' || c == '>')
        {
            op = std::string(1, c);
            len = 1;
            if (i + 1 < s.size() && s[i + 1] == '=') { op += "="; len = 2; }
        }
        else if (c == '=')
        {
            op = "="; len = 1;
        }
        else
        {
            continue;
        }

        if (found++ > 0) return false;
        if (found > 1) return false;

        out.left = s.substr(0, i);
        out.op = op;
        out.right = s.substr(i + len);
        i += len - 1;
    }

    return found == 1 && !out.left.empty() && !out.right.empty();
}

Val parseSingle(const std::string& text)
{
    Parser p(text);
    Val v = p.parseExpression();
    p.ensureEnd();
    return v;
}

std::string formatEquationRoots(const std::vector<double>& roots)
{
    if (roots.empty()) return "无实数解";

    std::string out;
    for (std::size_t i = 0; i < roots.size(); ++i)
    {
        if (i) out += " 或 ";
        out += "x = " + formatNumber(roots[i]);
    }
    return out;
}

std::string formatSolution(const std::vector<Interval>& intervals)
{
    if (intervals.empty()) return "无解";

    std::string out;
    for (std::size_t i = 0; i < intervals.size(); ++i)
    {
        if (i) out += " ∪ ";
        out += formatInterval(intervals[i]);
    }
    return "x ∈ " + out;
}

void appendHalfLine(std::vector<Interval>& out, double bound, bool leftSide, bool inclusive)
{
    Interval i;
    if (leftSide)
    {
        i.lo = -std::numeric_limits<double>::infinity();
        i.hi = bound;
        i.loOpen = true;
        i.hiOpen = !inclusive;
    }
    else
    {
        i.lo = bound;
        i.hi = std::numeric_limits<double>::infinity();
        i.loOpen = !inclusive;
        i.hiOpen = true;
    }
    out.push_back(i);
}

std::string solveComparison(const Comparison& cmp)
{
    Val l = parseSingle(cmp.left);
    Val r = parseSingle(cmp.right);

    Poly diff = polySub(
        l.kind == Kind::Number ? Poly{l.number,0,0} : (l.kind == Kind::Polynomial ? l.poly : throw std::runtime_error("interval in comparison")),
        r.kind == Kind::Number ? Poly{r.number,0,0} : (r.kind == Kind::Polynomial ? r.poly : throw std::runtime_error("interval in comparison")));

    std::string op = cmp.op;

    if (isZero(diff.c2))
    {
        if (isZero(diff.c1))
        {
            bool holds = op == "=" ? isZero(diff.c0)
                       : op == "<" ? diff.c0 < 0
                       : op == "<=" ? diff.c0 <= 0
                       : op == ">" ? diff.c0 > 0
                       : diff.c0 >= 0;
            if (op == "=") return holds ? "恒等式" : "无解";
            return holds ? "x ∈ (-∞, +∞)" : "无解";
        }

        double root = -diff.c0 / diff.c1;

        if (op == "=") return "x = " + formatNumber(root);

        bool increasing = diff.c1 > 0;
        std::vector<Interval> result;

        // 先按 c1>0 判断，c1<0 时把方向翻转
        std::string effective = op;
        if (!increasing)
        {
            if (op == "<") effective = ">";
            else if (op == "<=") effective = ">=";
            else if (op == ">") effective = "<";
            else if (op == ">=") effective = "<=";
        }

        if (effective == "<") appendHalfLine(result, root, true, false);
        else if (effective == "<=") appendHalfLine(result, root, true, true);
        else if (effective == ">") appendHalfLine(result, root, false, false);
        else appendHalfLine(result, root, false, true);

        return formatSolution(result);
    }

    // 二次：统一成 a>0
    double a = diff.c2, b = diff.c1, c = diff.c0;
    if (a < 0)
    {
        a = -a; b = -b; c = -c;
        if (op == "<") op = ">";
        else if (op == "<=") op = ">=";
        else if (op == ">") op = "<";
        else if (op == ">=") op = "<=";
    }

    double disc = b * b - 4 * a * c;
    std::vector<double> roots;

    if (disc > kEps)
    {
        double sq = std::sqrt(disc);
        double r1 = (-b - sq) / (2 * a);
        double r2 = (-b + sq) / (2 * a);
        if (r1 > r2) std::swap(r1, r2);
        roots.push_back(r1); roots.push_back(r2);
    }
    else if (std::fabs(disc) <= kEps)
    {
        roots.push_back(-b / (2 * a));
    }

    if (op == "=") return formatEquationRoots(roots);

    std::vector<Interval> result;

    if (roots.empty())   // disc < 0，a>0 时恒正
    {
        bool always = (op == ">" || op == ">=");
        return always ? "x ∈ (-∞, +∞)" : "无解";
    }

    if (roots.size() == 2)
    {
        double r1 = roots[0], r2 = roots[1];
        if (op == "<") { Interval i; i.lo=r1; i.hi=r2; i.loOpen=true; i.hiOpen=true; result.push_back(i); }
        else if (op == "<=") { Interval i; i.lo=r1; i.hi=r2; i.loOpen=false; i.hiOpen=false; result.push_back(i); }
        else if (op == ">") { appendHalfLine(result, r1, true, false); appendHalfLine(result, r2, false, false); }
        else { appendHalfLine(result, r1, true, true); appendHalfLine(result, r2, false, true); }
    }
    else
    {
        double r = roots[0];
        if (op == ">")
        {
            appendHalfLine(result, r, true, false);
            appendHalfLine(result, r, false, false);
        }
        else if (op == ">=")
        {
            return "x ∈ (-∞, +∞)";
        }
        else if (op == "<=")
        {
            Interval i; i.lo = i.hi = r; i.loOpen = false; i.hiOpen = false; result.push_back(i);
        }
        // op == "<" 时无解
    }

    return formatSolution(result);
}

std::string executeText(const std::string& expr)
{
    Comparison cmp;
    if (splitComparison(expr, cmp))
        return solveComparison(cmp);

    Val v = parseSingle(expr);
    if (v.kind == Kind::Number) return formatNumber(v.number);
    if (v.kind == Kind::Interval) return formatInterval(v.interval);
    throw std::runtime_error("未指定变量 x 的求解目标");
}

} // namespace

extern "C" {

int sympy_validate(const char* expr_utf8, char** normalized_utf8, char** error_utf8)
{
    if (!expr_utf8 || !normalized_utf8) return 2;

    std::string text(expr_utf8);
    // 去首尾空白
    auto begin = text.find_first_not_of(" \t\r\n");
    auto end = text.find_last_not_of(" \t\r\n");
    text = begin == std::string::npos ? std::string() : text.substr(begin, end - begin + 1);

    try
    {
        Comparison cmp;
        if (splitComparison(text, cmp))
        {
            parseSingle(cmp.left);
            parseSingle(cmp.right);
        }
        else
        {
            parseSingle(text);
        }
    }
    catch (const std::exception& ex)
    {
        if (error_utf8)
        {
            std::string msg = ex.what();
            *error_utf8 = static_cast<char*>(std::malloc(msg.size() + 1));
            if (*error_utf8) std::memcpy(*error_utf8, msg.c_str(), msg.size() + 1);
        }
        return 1;
    }

    *normalized_utf8 = static_cast<char*>(std::malloc(text.size() + 1));
    if (!*normalized_utf8) return 2;
    std::memcpy(*normalized_utf8, text.c_str(), text.size() + 1);
    return 0;
}

int sympy_execute(const char* expr_utf8, char** result_utf8, char** proof_utf8, char** error_utf8)
{
    if (!expr_utf8 || !result_utf8) return 2;

    std::string text(expr_utf8);
    auto begin = text.find_first_not_of(" \t\r\n");
    auto end = text.find_last_not_of(" \t\r\n");
    text = begin == std::string::npos ? std::string() : text.substr(begin, end - begin + 1);

    try
    {
        std::string result = executeText(text);
        *result_utf8 = static_cast<char*>(std::malloc(result.size() + 1));
        if (!*result_utf8) return 2;
        std::memcpy(*result_utf8, result.c_str(), result.size() + 1);

        if (proof_utf8)
        {
            const char* proof = "{}";
            *proof_utf8 = static_cast<char*>(std::malloc(3));
            if (*proof_utf8) std::memcpy(*proof_utf8, proof, 3);
        }
        return 0;
    }
    catch (const std::exception& ex)
    {
        if (error_utf8)
        {
            std::string msg = ex.what();
            *error_utf8 = static_cast<char*>(std::malloc(msg.size() + 1));
            if (*error_utf8) std::memcpy(*error_utf8, msg.c_str(), msg.size() + 1);
        }
        return 1;
    }
}

void sympy_free_string(char* s) { std::free(s); }

}
