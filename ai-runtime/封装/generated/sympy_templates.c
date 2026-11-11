/* generated/sympy_templates.c
 *
 * 构建时由 sympy2c / sympy_codegen 注入的模板集合。当前为手写的最小可用集合，
 * 供 dispatch 的模板分发表与冒烟测试使用；部署时应替换为 CI 生成的版本。
 *
 * 注意：新增模板必须同时更新 SYMPY_TEMPLATE_NAMES、SYMPY_TEMPLATE_COUNT
 * 以及 sympy_engine_dispatch.c 中的分派 switch。
 */

#include <math.h>

const char* const SYMPY_TEMPLATE_NAMES[] = {
    "pythagorean",
    "quadratic_root",
};
const int SYMPY_TEMPLATE_COUNT = 2;

/* 勾股定理：sqrt(a^2 + b^2) */
double tmpl_pythagorean(double a, double b)
{
    return sqrt(a * a + b * b);
}

/* x^2 + b*x + a = 0 的正根：(-b + sqrt(b^2 - 4a)) / 2 */
double tmpl_quadratic_root(double a, double b)
{
    double disc = b * b - 4.0 * a;
    if (disc < 0) return sqrt(-1.0);   /* 无实根：返回 NaN，由上层按 ERROR 处理 */
    return (-b + sqrt(disc)) / 2.0;
}
