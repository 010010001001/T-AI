/* generated/sympy_engine_dispatch.c
 *
 * sympy_engine.dll 的 C ABI 实现。
 *
 * 职责：
 *   1. 中缀表达式的递归下降求值（数字、+ - * / ^、括号、逗号分隔函数参数）；
 *   2. 内置数学函数与构建时注入的 SymPy 模板；
 *   3. JSON 结果与错误信息构造；
 *   4. sympy_free_string 的实现。
 *
 * 支持的模板由 generated/sympy_templates.c 在构建时注入：
 *   extern double tmpl_<name>(double x, double y);
 *   extern const char* const SYMPY_TEMPLATE_NAMES[];
 *   extern const int SYMPY_TEMPLATE_COUNT;
 *
 * 不支持：符号变量、代数求解、微积分、矩阵。这些在 sympy2c 构建期展开，
 * 运行时不携带 Python 解释器。
 */

#include "sympy_engine.h"

#include <ctype.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ============================================================================
 * 构建时注入的模板声明
 * ========================================================================== */
extern double tmpl_pythagorean(double x, double y);
extern double tmpl_quadratic_root(double x, double y);

extern const char* const SYMPY_TEMPLATE_NAMES[];
extern const int SYMPY_TEMPLATE_COUNT;

/* ============================================================================
 * 内存管理
 * ========================================================================== */

void sympy_free_string(char* s) { free(s); }

static char* xstrdup(const char* s)
{
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char* p = (char*)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}

/* ============================================================================
 * 递归下降求值器
 *
 *   expr    := term (('+' | '-') term)*
 *   term    := unary (('*' | '/') unary)*
 *   unary   := ('+' | '-') unary | power
 *   power   := primary ('^' unary)?
 *   primary := number | ident '(' args ')' | ident | '(' expr ')'
 * ========================================================================== */

typedef struct
{
    const char* p;
    int error;   /* 0 = ok, 1 = unsupported, 2 = error */
} Parser;

static void skip_ws(Parser* ps)
{
    while (*ps->p && isspace((unsigned char)*ps->p)) ++ps->p;
}

static double parse_expr(Parser* ps);

static double fail(Parser* ps, int code)
{
    if (ps->error == 0) ps->error = code;
    return 0.0;
}

static int match_char(Parser* ps, char c)
{
    skip_ws(ps);
    if (*ps->p == c) { ++ps->p; return 1; }
    return 0;
}

static double parse_primary(Parser* ps);

/* 调用内置函数或构建时注入的模板。argc 只允许 1 或 2。 */
static double call_function(Parser* ps, const char* name, double* args, int argc)
{
    if (argc <= 0 || argc > 2) return fail(ps, 1);

    if (strcmp(name, "sqrt") == 0 && argc == 1)
    {
        if (args[0] < 0) return fail(ps, 2);
        return sqrt(args[0]);
    }
    if (strcmp(name, "abs") == 0 && argc == 1) return fabs(args[0]);
    if (strcmp(name, "exp") == 0 && argc == 1) return exp(args[0]);
    if (strcmp(name, "log") == 0 && argc == 1)
    {
        if (args[0] <= 0) return fail(ps, 2);
        return log(args[0]);
    }
    if (strcmp(name, "sin") == 0 && argc == 1) return sin(args[0]);
    if (strcmp(name, "cos") == 0 && argc == 1) return cos(args[0]);
    if (strcmp(name, "tan") == 0 && argc == 1) return tan(args[0]);
    if (strcmp(name, "pow") == 0 && argc == 2) return pow(args[0], args[1]);
    if (strcmp(name, "max") == 0 && argc == 2) return args[0] > args[1] ? args[0] : args[1];
    if (strcmp(name, "min") == 0 && argc == 2) return args[0] < args[1] ? args[0] : args[1];

    for (int i = 0; i < SYMPY_TEMPLATE_COUNT; ++i)
    {
        if (strcmp(name, SYMPY_TEMPLATE_NAMES[i]) != 0) continue;
        if (argc != 2) return fail(ps, 1);

        if (strcmp(name, "pythagorean") == 0) return tmpl_pythagorean(args[0], args[1]);
        if (strcmp(name, "quadratic_root") == 0) return tmpl_quadratic_root(args[0], args[1]);
        return fail(ps, 1);
    }

    return fail(ps, 1);
}

static double parse_primary(Parser* ps)
{
    skip_ws(ps);

    if (*ps->p == '(')
    {
        ++ps->p;
        double v = parse_expr(ps);
        if (!match_char(ps, ')')) return fail(ps, 2);
        return v;
    }

    if (isdigit((unsigned char)*ps->p) || *ps->p == '.')
    {
        char* end = NULL;
        double v = strtod(ps->p, &end);
        if (end == ps->p) return fail(ps, 2);
        ps->p = end;
        return v;
    }

    if (isalpha((unsigned char)*ps->p) || *ps->p == '_')
    {
        char name[64];
        size_t n = 0;
        while ((isalnum((unsigned char)*ps->p) || *ps->p == '_') && n < sizeof(name) - 1)
            name[n++] = *ps->p++;
        name[n] = '\0';

        skip_ws(ps);
        if (*ps->p != '(')
        {
            if (strcmp(name, "pi") == 0) return 3.14159265358979323846;
            if (strcmp(name, "e") == 0) return 2.71828182845904523536;
            return fail(ps, 1);   /* 裸标识符不支持（无符号变量） */
        }

        ++ps->p;   /* '(' */

        double args[2] = { 0, 0 };
        int argc = 0;

        skip_ws(ps);
        if (*ps->p == ')')
        {
            ++ps->p;
            return fail(ps, 1);   /* 零参数函数不支持 */
        }

        for (;;)
        {
            double v = parse_expr(ps);
            if (ps->error) return 0;
            if (argc < 2) args[argc] = v;
            ++argc;

            skip_ws(ps);
            if (*ps->p == ',') { ++ps->p; continue; }
            if (*ps->p == ')') { ++ps->p; break; }
            return fail(ps, 2);
        }

        return call_function(ps, name, args, argc);
    }

    return fail(ps, 2);
}

static double parse_unary(Parser* ps)
{
    skip_ws(ps);
    if (*ps->p == '+') { ++ps->p; return parse_unary(ps); }
    if (*ps->p == '-') { ++ps->p; return -parse_unary(ps); }
    return parse_primary(ps);
}

static double parse_power(Parser* ps)
{
    double base = parse_unary(ps);
    if (ps->error) return 0;

    skip_ws(ps);
    if (*ps->p == '^')
    {
        ++ps->p;
        double exponent = parse_power(ps);   /* 右结合 */
        if (ps->error) return 0;
        return pow(base, exponent);
    }
    return base;
}

static double parse_term(Parser* ps)
{
    double v = parse_power(ps);
    if (ps->error) return 0;

    for (;;)
    {
        skip_ws(ps);
        if (*ps->p == '*')
        {
            ++ps->p;
            double rhs = parse_power(ps);
            if (ps->error) return 0;
            v *= rhs;
        }
        else if (*ps->p == '/')
        {
            ++ps->p;
            double rhs = parse_power(ps);
            if (ps->error) return 0;
            if (rhs == 0.0) return fail(ps, 2);
            v /= rhs;
        }
        else
        {
            return v;
        }
    }
}

static double parse_expr(Parser* ps)
{
    double v = parse_term(ps);
    if (ps->error) return 0;

    for (;;)
    {
        skip_ws(ps);
        if (*ps->p == '+')
        {
            ++ps->p;
            double rhs = parse_term(ps);
            if (ps->error) return 0;
            v += rhs;
        }
        else if (*ps->p == '-')
        {
            ++ps->p;
            double rhs = parse_term(ps);
            if (ps->error) return 0;
            v -= rhs;
        }
        else
        {
            return v;
        }
    }
}

static int evaluate(const char* expr, double* out)
{
    Parser ps = { expr, 0 };
    double v = parse_expr(&ps);

    if (ps.error) return ps.error;

    skip_ws(&ps);
    if (*ps.p != '\0') return 2;   /* 尾随垃圾字符 */

    *out = v;
    return 0;
}

/* ============================================================================
 * 导出的 C ABI
 * ========================================================================== */

int sympy_validate(const char* expr_utf8,
                   char** normalized_utf8, char** error_utf8)
{
    if (!expr_utf8 || !normalized_utf8) return 2;

    const char* s = expr_utf8;
    while (*s && isspace((unsigned char)*s)) ++s;

    size_t len = strlen(s);
    while (len > 0 && isspace((unsigned char)s[len - 1])) --len;

    char* norm = (char*)malloc(len + 1);
    if (!norm) return 2;
    memcpy(norm, s, len);
    norm[len] = '\0';

    double value = 0;
    int rc = evaluate(norm, &value);
    if (rc != 0)
    {
        if (error_utf8)
        {
            *error_utf8 = xstrdup(rc == 1 ? "unsupported expression"
                                          : "invalid expression syntax");
        }
        free(norm);
        return rc;
    }

    *normalized_utf8 = norm;
    return 0;
}

int sympy_execute(const char* expr_utf8,
                  char** result_utf8, char** proof_utf8, char** error_utf8)
{
    if (!expr_utf8 || !result_utf8) return 2;

    double value = 0;
    int rc = evaluate(expr_utf8, &value);

    if (rc != 0)
    {
        if (error_utf8)
        {
            *error_utf8 = xstrdup(rc == 1 ? "unsupported expression"
                                          : "evaluation error");
        }
        return rc;
    }

    char valbuf[64];
    if (isnan(value)) snprintf(valbuf, sizeof(valbuf), "NaN");
    else if (isinf(value)) snprintf(valbuf, sizeof(valbuf), value > 0 ? "Infinity" : "-Infinity");
    else snprintf(valbuf, sizeof(valbuf), "%.15g", value);

    *result_utf8 = xstrdup(valbuf);

    if (proof_utf8)
    {
        char proofbuf[32];
        snprintf(proofbuf, sizeof(proofbuf), "{}");
        *proof_utf8 = xstrdup(proofbuf);
    }

    return 0;
}
