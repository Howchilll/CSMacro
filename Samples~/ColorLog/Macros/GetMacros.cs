using System;
using System.Linq;
using HowMacro;

namespace ColorLog.Macros
{
    /// <summary>
    /// <c>[@Get] Rigidbody body;</c>: filled by <c>@GET_ALL();</c> (call it in Awake) with GetComponent on the same object.
    /// <c>[@Get(inChildren: true)]</c> searches children too. The field must be a Component type (compile error otherwise);
    /// a missing component is logged as an error naming the object and the field.
    /// </summary>
    [AttributeMacro(MacroTarget.Field)]
    public sealed class Get : Attribute
    {
        public Get(bool inChildren = false)
        {
            var t = Macro.Declaration?.TypeInfo;
            var isComponent = false;
            for (var b = t; b != null; b = b.BaseType) if (b.FullName == "UnityEngine.Component") isComponent = true;
            if (!isComponent) { Macro.Error(Macro.Name + ": " + Macro.Type + " is not a Component, so GetComponent can't find it"); return; }
            if (Macro.Declaration.IsStatic) { Macro.Error(Macro.Name + " must not be static"); return; }
            Macro.Fragment(GetMacros.Channel, Macro.ArgCount > 0 && (bool)Macro.ArgValue(0) ? "children" : "self");
        }
    }

    public static class GetMacros
    {
        internal const string Channel = "get";

        /// <summary><c>void Awake() { @GET_ALL(); }</c> assigns every [@Get] field of this class.</summary>
        [Macro(MacroSite.Statement)]
        public static void GET_ALL()
        {
            foreach (var f in Macro.Fragments(Channel).Where(f => f.ContainingType == Macro.ContainingType))
            {
                var get = f.Code == "children" ? "GetComponentInChildren" : "GetComponent";
                var type = f.Declaration?.TypeInfo?.CodeName ?? f.MemberType; // global::UnityEngine.Renderer: needs no using
                Macro.Emit("this." + f.MemberName + " = " + get + "<" + type + ">(); ");
                Macro.Emit("if (this." + f.MemberName + " == null) global::UnityEngine.Debug.LogError(" +
                           Macro.Quote("[@Get] " + f.MemberName + ": no " + f.MemberType + " on ") + " + name, this);");
            }
        }
    }
}
