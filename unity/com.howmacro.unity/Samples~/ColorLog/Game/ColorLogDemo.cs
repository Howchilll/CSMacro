using ColorLog.Macros;
using UnityEngine;
using static ColorLog.Macros.ColorLogMacro;
using static ColorLog.Macros.GetMacros;
using static ColorLog.Macros.SaveMacros;
using static ColorLog.Macros.WatchMacros;

namespace ColorLog.Game
{
    /// <summary>
    /// Put it on a Cube. value1 and value2 log every change in color and go up by 1 every five seconds; they are saved
    /// when you quit and loaded on start; a debug box shows them live; the cube's color follows value1.
    /// Every one of those is a macro: the attributes mark members, the @..._ALL / @WATCH_GUI calls generate the code.
    /// </summary>
    public sealed class ColorLogDemo : MonoBehaviour
    {
        [@Save][@Watch] public int value1 = @COLORLOG(0, Color.blue);
        [@Save][@Watch] public int value2 = @COLORLOG(0, new Color(1f, 0.6f, 0.2f));
        [@Watch("next +1 in (s)")] float NextTick => 5f - _timer;

        [@Get] Renderer _renderer;
        float _timer;

        void Awake()
        {
            @GET_ALL();
        }

        void Start()
        {
            value1 = 1;
            value2 = 100;
            @LOAD_ALL(); // values saved by an earlier run win
        }

        void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < 5f) return;
            _timer -= 5f;
            value1++;
            value2++;
            if (_renderer != null) _renderer.material.color = Color.HSVToRGB(value1 % 10 / 10f, 0.5f, 1f);
        }

        void OnGUI()
        {
            @WATCH_GUI();
        }

        void OnApplicationQuit()
        {
            @SAVE_ALL();
        }
    }
}
