using System.Diagnostics;
using System.Runtime.CompilerServices;
using Demo.App;
using static Demo.Macros.M;

var p = new Player();

// GETSET：写 Player.Hp 实际写的是 DataCenter.PlayerData.Hp
p.Hp -= 30;
@LOG(DataCenter.PlayerData.Hp);
@ASSERT(DataCenter.PlayerData.Hp == 70);

// Mp 同样转发
DataCenter.PlayerData.Mp = 7;
@LOG(p.Mp);

// LOGGED：统一的"改变即打 log"
p.Gold += 50;
p.Level++;
p.Title = "Hero";
p.Title = "Hero"; // 值没变，不打 log

// 宏不能包含宏：要组合时先展开到局部变量
var sq = @SQUARE(4);
@LOG(sq);

// [@Save]：启动时已自动登记（辅助脚本生成），存档容器里没有任何手写的登记代码
var save = SaveContainer.Save(p);
Console.WriteLine("save: " + string.Join(", ", save.Select(kv => $"{kv.Key}={kv.Value}")));
p.Gold = 0;
SaveContainer.Load(p, save);

// [@Track] + @DUMP_TRACKED：方法宏汇总所有特性宏产出的片段
Console.WriteLine("tracked: " + @DUMP_TRACKED(p));

// @DUMP：宏用语义信息遍历 Player 的成员，编译期生成拼接代码（运行时不用反射）
Console.WriteLine("dump: " + @DUMP(p));

// 编译期生成 switch
for (var i = 0; i < 3; i++)
    Console.WriteLine(@NAME_OF_INDEX(i, "Red", "Green"));

// 跨多行的宏调用：展开后压成一行，并补回换行
@LOG(
    p.Hp +
    p.Mp);

// 行号检查：@LINE() 是原文件行号，Here() 是编译器认为的行号，两者必须相等
Console.WriteLine($"line check: macro={@LINE()} compiler={Here()}");

// 栈跟踪检查：异常里的文件和行号应指向原文件（Program.cs），而不是 obj 里的展开副本
try
{
    @ASSERT(p.Hp < 0);
}
catch (InvalidOperationException e)
{
    var frame = new StackTrace(e, fNeedFileInfo: true).GetFrame(0)!;
    Console.WriteLine($"assert: {e.Message}");
    Console.WriteLine($"stack frame: {Path.GetFileName(frame.GetFileName())}:{frame.GetFileLineNumber()}");
}

static int Here([CallerLineNumber] int line = 0) => line;
