#!/usr/bin/env python3
"""Generates banner.html, ig-en.html and ig-zh.html, then renders the PNGs with headless Chrome."""
import pathlib, subprocess, sys

HERE = pathlib.Path(__file__).resolve().parent
CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"

def define(comment):
    return f'''<span class="com">// {comment}</span>
[<span class="ty">Macro</span>(<span class="ty">MacroSite</span>.Member)]
<span class="kw">public static</span> T <span class="fn">LOGGED</span>&lt;T&gt;(T initial)
{{
    <span class="kw">string</span> t = <span class="ty">Macro</span>.Type, n = <span class="ty">Macro</span>.Name, s = <span class="str">"@_temp_"</span> + n;
    <span class="ty">Macro</span>.<span class="fn">Emit</span>(<span class="str">$"private {{t}} {{s}} = {{</span><span class="ty">Macro</span>.<span class="fn">Arg</span>(<span class="num">0</span>)<span class="str">}};"</span>);
    <span class="ty">Macro</span>.<span class="fn">Emit</span>(<span class="str">$"public {{t}} {{n}} {{{{ get =&gt; {{s}}; set {{{{ "</span> +
               <span class="str">$"Log(\\"{{n}}\\", value); {{s}} = value; }}}} }}}}"</span>);
    <span class="kw">return default</span>;
}}'''

USE = '''[<span class="m">@Save</span>] <span class="kw">public int</span> gold  = <span class="m">@LOGGED</span>(<span class="num">100</span>);
[<span class="m">@Save</span>] <span class="kw">public int</span> level = <span class="m">@LOGGED</span>(<span class="num">1</span>);
<span class="kw">void</span> <span class="fn">OnApplicationQuit</span>() { <span class="m">@SAVE_ALL</span>(); }'''

def gets(likewise):
    return f'''<span class="kw">private int</span> _temp_gold = <span class="num">100</span>;
<span class="kw">public int</span> gold {{ <span class="kw">get</span> =&gt; _temp_gold;
    <span class="kw">set</span> {{ <span class="fn">Log</span>(<span class="str">"gold"</span>, <span class="kw">value</span>); _temp_gold = <span class="kw">value</span>; }} }}
<span class="com">// {likewise}</span>
<span class="kw">void</span> <span class="fn">OnApplicationQuit</span>() {{
    <span class="ty">PlayerPrefs</span>.<span class="fn">SetInt</span>(<span class="str">"Player.gold"</span>, gold);
    <span class="ty">PlayerPrefs</span>.<span class="fn">SetInt</span>(<span class="str">"Player.level"</span>, level);
    <span class="ty">PlayerPrefs</span>.<span class="fn">Save</span>(); }}'''

TEXT = {
    "en": dict(
        headline='Build your own<br><span class="grad">compile&#8209;time abstractions</span><br>in C#',
        tag="Not just less code: fold complex logic into macros, keep the source readable.",
        vals=[("Compress complexity", "Fold repeated structures that functions, generics and patterns can't abstract cleanly."),
              ("IDE&#8209;native", "Macros are plain C#: readable, indexed, F12 jumps to the definition."),
              ("Source stays yours", "You maintain the unexpanded code. Expansion happens at build time; no generated code to keep."),
              ("Your own macro system", "Package team conventions, business rules and domain abstractions as macros.")],
        define="Macros/LogMacros.cs: a macro is plain C#",
        s1="Define", s2="Use", s3="Compiler gets", f2="Player.cs", f3="expanded at build time",
        likewise="… level likewise", h1ig="54px"),
    "zh": dict(
        headline='赋予 <span class="grad">C#</span><br><span class="grad">自定义编译期抽象</span>的能力',
        tag="不只是减少代码量：把复杂业务逻辑收束进宏，同时保持代码的可读性和可维护性。",
        vals=[("复杂性收束", "收拢函数、泛型和设计模式难以优雅抽象的重复代码结构。"),
              ("IDE 原生体验", "宏定义就是可读、可索引的 C# 代码，F12 直达定义。"),
              ("源代码可读", "只维护未展开的源代码，复杂实现由编译期自动展开，无需维护生成代码。"),
              ("开发者自定义", "把团队规范、业务逻辑和领域抽象封装成自己的宏体系。")],
        define="Macros/LogMacros.cs：宏就是普通 C#",
        s1="定义宏", s2="使用", s3="编译器拿到", f2="Player.cs", f3="构建时自动展开",
        likewise="… level 同理", h1ig="62px"),
}

def vals_html(t):
    return "\n".join(f'<div class="val"><span class="n">0{i}</span><h3>{h}</h3><p>{p}</p></div>'
                     for i, (h, p) in enumerate(t["vals"], 1))

def card(cls, step, title, file, code):
    return (f'<div class="card {cls}"><div class="bar"><span class="dot"></span><span class="dot"></span><span class="dot"></span>'
            f'<span class="label"><span class="step">{step}</span><b>{title}</b>{" · " + file if file else ""}</span></div>\n<pre>{code}</pre></div>')

def cards(t):
    return "\n".join([card("c1", "①", t["s1"], "", define(t["define"])),
                      card("c2", "②", t["s2"], t["f2"], USE),
                      card("c3", "③", t["s3"], t["f3"], gets(t["likewise"]))])

FOOT = '<div class="foot"><span><b>github.com/Howchilll/CSMacro</b></span><span>NuGet · UPM</span></div>'

def banner(t):
    return f'''<!doctype html><html lang="en"><head><meta charset="utf-8"><link rel="stylesheet" href="promo.css">
<style>
  body {{ width: 1280px; height: 640px; padding: 40px 48px; display: grid; grid-template-columns: 500px 1fr; column-gap: 40px; }}
  .g1 {{ width: 420px; height: 420px; left: -140px; top: -160px; }} .g2 {{ width: 460px; height: 460px; right: -160px; bottom: -220px; }}
  .left {{ display: flex; flex-direction: column; }}
  .mark {{ width: 46px; height: 46px; font-size: 27px; }} .name {{ font-size: 30px; }}
  h1 {{ font-size: 40px; margin-top: 22px; }} .tag {{ font-size: 16.5px; margin-top: 12px; }}
  .vals {{ margin-top: 22px; row-gap: 12px; }} .val .n {{ font-size: 14px; }} .val h3 {{ font-size: 18px; }} .val p {{ font-size: 13.5px; }}
  .foot {{ margin-top: auto; display: flex; justify-content: space-between; font-size: 13.5px; }}
  .right {{ display: flex; flex-direction: column; gap: 10px; }}
  .card .bar {{ font-size: 13px; padding: 8px 14px; }} .dot {{ width: 9px; height: 9px; }}
  pre {{ font-size: 12.4px; line-height: 1.45; padding: 9px 16px; }}
</style></head><body>
<div class="glow g1"></div><div class="glow g2"></div>
<div class="left">
<div class="brand"><div class="mark">@</div><div class="name">HowMacro</div></div>
<h1>{t["headline"]}</h1>
<div class="tag">{t["tag"]}</div>
<div class="vals">{vals_html(t)}</div>
{FOOT}
</div>
<div class="right">{cards(t)}</div>
</body></html>'''

def ig(t, lang):
    return f'''<!doctype html><html lang="{lang}"><head><meta charset="utf-8"><link rel="stylesheet" href="promo.css">
<style>
  body {{ width: 1080px; height: 1350px; padding: 64px 64px 48px; display: flex; flex-direction: column; }}
  .g1 {{ width: 560px; height: 560px; left: -200px; top: -220px; }} .g2 {{ width: 620px; height: 620px; right: -260px; bottom: -240px; }}
  .mark {{ width: 56px; height: 56px; font-size: 33px; }} .name {{ font-size: 36px; }}
  h1 {{ font-size: {t["h1ig"]}; margin-top: 26px; }} .tag {{ font-size: 22px; margin-top: 14px; }}
  .vals {{ margin-top: 24px; grid-template-columns: 1fr 1fr; gap: 14px 32px; align-items: start; }}
  .val .n {{ font-size: 16px; }} .val h3 {{ font-size: 22px; }} .val p {{ font-size: 16px; }}
  .right {{ margin-top: 24px; display: flex; flex-direction: column; gap: 12px; }}
  .card .bar {{ font-size: 16px; padding: 10px 18px; }}
  pre {{ font-size: 15px; line-height: 1.5; padding: 10px 20px; }}
  .foot {{ margin-top: auto; padding-top: 12px; display: flex; justify-content: space-between; font-size: 19px; }}
</style></head><body>
<div class="glow g1"></div><div class="glow g2"></div>
<div class="brand"><div class="mark">@</div><div class="name">HowMacro</div></div>
<h1>{t["headline"]}</h1>
<div class="tag">{t["tag"]}</div>
<div class="vals">{vals_html(t)}</div>
<div class="right">{cards(t)}</div>
{FOOT}
</body></html>'''

def render(html, png, w, h, scale):
    subprocess.run([CHROME, "--headless=new", "--hide-scrollbars", f"--force-device-scale-factor={scale}",
                    f"--window-size={w},{h}", f"--screenshot={HERE / png}", (HERE / html).as_uri()],
                   check=True, stderr=subprocess.DEVNULL)

(HERE / "banner.html").write_text(banner(TEXT["en"]), encoding="utf-8")
(HERE / "banner-zh.html").write_text(banner(TEXT["zh"]), encoding="utf-8")
(HERE / "ig-en.html").write_text(ig(TEXT["en"], "en"), encoding="utf-8")
(HERE / "ig-zh.html").write_text(ig(TEXT["zh"], "zh-CN"), encoding="utf-8")
if "--no-render" not in sys.argv:
    render("banner.html", "banner.png", 1280, 640, 2)
    render("banner-zh.html", "banner-zh.png", 1280, 640, 2)
    render("ig-en.html", "instagram-en.png", 1080, 1350, 1)
    render("ig-zh.html", "instagram-zh.png", 1080, 1350, 1)
