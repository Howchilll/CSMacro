using System.Diagnostics;
using System.Runtime.CompilerServices;
using Demo.App;
using static Demo.Macros.M;

var p = new Player();

// GETSET: writing Player.Hp actually writes DataCenter.PlayerData.Hp
p.Hp -= 30;
@LOG(DataCenter.PlayerData.Hp);
@ASSERT(DataCenter.PlayerData.Hp == 70);

// Mp forwards the same way
DataCenter.PlayerData.Mp = 7;
@LOG(p.Mp);

// LOGGED: one shared "log every change" rule
p.Gold += 50;
p.Level++;
p.Title = "Hero";
p.Title = "Hero"; // unchanged, no log

// Macros can't contain macros: to combine them, expand into a local variable first
var sq = @SQUARE(4);
@LOG(sq);

// [@Save]: registered automatically at startup (generated startup script); the save container has no hand-written registration code
var save = SaveContainer.Save(p);
Console.WriteLine("save: " + string.Join(", ", save.Select(kv => $"{kv.Key}={kv.Value}")));
p.Gold = 0;
SaveContainer.Load(p, save);

// [@Track] + @DUMP_TRACKED: a method macro collects the fragments produced by an attribute macro
Console.WriteLine("tracked: " + @DUMP_TRACKED(p));

// @DUMP: the macro walks Player's members via semantic information and generates the concatenation at compile time (no runtime reflection)
Console.WriteLine("dump: " + @DUMP(p));

// Region macro #region @LOG_ALL: every wrapped field in Enemy logs its changes
var enemy = new Enemy();
enemy.Hp -= 20;
enemy.State = "Chasing";
enemy.Attack = 8; // unchanged, no log

// A switch generated at compile time
for (var i = 0; i < 3; i++)
    Console.WriteLine(@NAME_OF_INDEX(i, "Red", "Green"));

// A macro call spanning several lines: folded onto one line after expansion, with the newlines added back
@LOG(
    p.Hp +
    p.Mp);

// Line check: @LINE() is the source line, Here() is the line the compiler sees; they must match
Console.WriteLine($"line check: macro={@LINE()} compiler={Here()}");

// Stack trace check: the file and line in the exception should point at the source (Program.cs), not the expanded copy in obj
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
