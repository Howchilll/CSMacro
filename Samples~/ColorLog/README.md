# ColorLog sample

Small, everyday macros for a MonoBehaviour. Import it (Package Manager → HowMacro for Unity → Samples → ColorLog), add
`ColorLogDemo` to a **Cube** and press Play.

```csharp
[@Save][@Watch] public int value1 = @COLORLOG(0, Color.blue);
[@Save][@Watch] public int value2 = @COLORLOG(0, new Color(1f, 0.6f, 0.2f));
[@Watch("next +1 in (s)")] float NextTick => 5f - _timer;
[@Get] Renderer _renderer;

void Awake()             { @GET_ALL(); }
void Start()             { value1 = 1; value2 = 100; @LOAD_ALL(); }
void OnGUI()             { @WATCH_GUI(); }
void OnApplicationQuit() { @SAVE_ALL(); }
```

| Macro | What it saves you | Checked at compile time |
|---|---|---|
| `@COLORLOG(initial, color)` | A field becomes a property that logs every change in color (`[ColorLogDemo.value1] 3 -> 4`). The value lives in a private field the macro adds, `@_temp_value1`, serialized under the old name so the Inspector and saved scenes keep working | — |
| `[@Get]` + `@GET_ALL()` | No more `GetComponent` boilerplate: every marked field is filled in Awake; a missing component is logged with the object and field. `[@Get(inChildren: true)]` searches children | The field must be a Component |
| `[@Save]` + `@SAVE_ALL()` / `@LOAD_ALL()` | PlayerPrefs code per member, by type; the key defaults to `Type.member` (`[@Save("key")]` to choose) | int/float/string/bool only; a key used twice anywhere is an error |
| `[@Watch]` + `@WATCH_GUI()` | A live debug box listing every marked member | Nothing is generated in release builds |

The pattern behind the last three: an attribute macro marks members and records a fragment; a method macro in the same
class collects the fragments and writes the code. Adding a saved or watched value is one attribute, with nothing else
to keep in sync.

Layout: `Tools/` (ColorfulLogger, runtime), `Macros/` (the macros; left out of player builds), `Game/` (the demo).
Needs com.howmacro.unity 0.1.5-preview or later (macros keeping private state).
Macros are meant to stay local: `value1`/`value2` are properties only inside `ColorLog.Game`; another assembly using
them directly fails at runtime (`MissingFieldException`), so bridge them in your own code if you need them elsewhere.
