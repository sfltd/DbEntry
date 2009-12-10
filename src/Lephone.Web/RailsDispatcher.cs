﻿using System;
using System.Collections.Generic;
using System.Web;
using System.Web.UI;
using System.Reflection;
using System.IO;
using Lephone.Util;
using Lephone.Web.Rails;

namespace Lephone.Web
{
    public class RailsDispatcher : IHttpHandler
    {
        protected PageHandlerFactory factory = ClassHelper.CreateInstance<PageHandlerFactory>();
        internal static Dictionary<string, Type> ctls;
        private static readonly char[] spliter = new[] { '/' };

        static RailsDispatcher()
        {
            ctls = new Dictionary<string, Type>();
            ctls["default"] = typeof(DefaultController);
            Type cbType = typeof(ControllerBase);
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if(a.FullName == null) { continue; }
                string s = a.FullName.Split(',')[0];
                if (!s.StartsWith("System.") && CouldBeControllerAssemebly(s))
                {
                    foreach (Type t in a.GetTypes())
                    {
                        if (t.IsSubclassOf(cbType))
                        {
                            string tn = t.Name;
                            if (tn.EndsWith("Controller"))
                            {
                                tn = tn.Substring(0, tn.Length - 10);
                            }
                            ctls[tn.ToLower()] = t;
                        }
                    }
                }
            }
        }

        private static bool CouldBeControllerAssemebly(string s)
        {
            switch(s)
            {
                case "Lephone.Data":
                case "Lephone.Util":
                case "Lephone.Web":
                case "mscorlib":
                case "System":
                    return false;
                default:
                    return true;
            }
        }

        public bool IsReusable
        {
            get { return true; }
        }

        public void ProcessRequest(HttpContext context)
        {
            string url = context.Request.AppRelativeCurrentExecutionFilePath;
            url = url.Substring(2);
            
            if (url.ToLower().EndsWith(".aspx"))
            {
                url = url.Substring(0, url.Length - 5);
            }

            string[] ss = url.Split(spliter, StringSplitOptions.RemoveEmptyEntries);
            for(int i = 0; i < ss.Length; i++)
            {
                ss[i] = HttpUtility.UrlDecode(ss[i]);
            }

            string ControllerName = (ss.Length == 0) ? "default" : ss[0].ToLower();

            if (ctls.ContainsKey(ControllerName))
            {
                InvokeAction(context, ControllerName, ss);
                return;
            }
            context.Response.StatusCode = 404;
        }

        protected virtual void InvokeAction(HttpContext context, string controllerName, string[] ss)
        {
            // Invoke Controller
            Type t = ctls[controllerName];
            var ctl = ClassHelper.CreateInstance(t) as ControllerBase;
            if(ctl == null)
            {
                throw new WebException("The Controller must inherits from ControllerBase");
            }
            ctl.ctx = context;

            try
            {
                ControllerInfo ci = ControllerInfo.GetInstance(t);
                string ActionName = ss.Length > 1 ? ss[1] : ci.DefaultAction;
                MethodInfo mi = GetMethodInfo(t, ActionName);
                if (mi == null)
                {
                    throw new WebException(string.Format("Action {0} doesn't exist!!!", ActionName));
                }

                List<object> parameters = GetParameters(ss, mi);
                object ret = CallAction(mi, ctl, parameters.ToArray()) ?? "";
                if(string.IsNullOrEmpty(ret.ToString()))
                {
                    // Invoke Viewer
                    PageBase p = CreatePage(context, ci, t, controllerName, ActionName);
                    if (p != null)
                    {
                        InitViewPage(controllerName, ctl, ActionName, p);
                        ((IHttpHandler)p).ProcessRequest(context);
                        factory.ReleaseHandler(p);
                    }
                }
                else
                {
                    context.Response.Redirect(ret.ToString());
                }
            }
            catch (TargetInvocationException ex)
            {
                ctl.OnException(ex);
            }
            catch (WebException ex)
            {
                ctl.OnException(ex);
            }
        }

        private static MethodInfo GetMethodInfo(Type t, string actionName)
        {
            MethodInfo mi = t.GetMethod(actionName, ClassHelper.InstancePublic | BindingFlags.DeclaredOnly | BindingFlags.IgnoreCase);
            if (mi != null) return mi;
            return t.GetMethod(actionName, ClassHelper.InstancePublic | BindingFlags.IgnoreCase);
        }

        private static void InitViewPage(string controllerName, ControllerBase ctl, string actionName, PageBase p)
        {
            p.bag = ctl.bag;
            p.ControllerName = controllerName;
            p.ActionName = actionName;
            p.InitFields();
        }

        private static List<object> GetParameters(string[] ss, MethodInfo mi)
        {
            ParameterInfo[] pis = mi.GetParameters();
            var parameters = new List<object>();
            for (int i = 0; i < pis.Length; i++)
            {
                if (i + 2 < ss.Length)
                {
                    if(pis[i].ParameterType.IsArray)
                    {
                        ProcessArray(parameters, ss, i + 2, pis, i);
                        break;
                    }
                    object px = ChangeType(ss[i + 2], pis[i].ParameterType);
                    parameters.Add(px);
                }
                else
                {
                    parameters.Add(null);
                }
            }
            return parameters;
        }

        public static void ProcessArray(List<object> parameters, string[] ss, int startIndex, ParameterInfo[] pis, int curIndex)
        {
            var list = new List<string>();
            int valuesCount = pis.Length - curIndex - 1;
            for (int i = startIndex; i < ss.Length - valuesCount; i++)
            {
                list.Add(ss[i]);
            }

            var values = new List<object>();
            int nIndex = ss.Length - valuesCount;
            for (int i = 0; i < valuesCount; i++)
            {
                Type t = pis[i + curIndex + 1].ParameterType;
                object px = ChangeType(ss[nIndex + i], t);
                values.Add(px);
            }

            parameters.Add(list.ToArray());
            parameters.AddRange(values);
        }

        private PageBase CreatePage(HttpContext context, ControllerInfo ci, Type t, string controllerName, string actionName)
        {
            string vp = context.Request.ApplicationPath + "/Views/" + controllerName + "/" + actionName + ".aspx";
            string pp = context.Server.MapPath(vp);
            if (File.Exists(pp))
            {
                object o = factory.GetHandler(context, context.Request.RequestType, vp, pp);
                if (o == null)
                {
                    throw new WebException("The template page must inherits from PageBase!!!");
                }
                return o as PageBase;
            }
            if (ci.IsScaffolding)
            {
                Type tt = GetScaffoldingType(t);
                return new ScaffoldingViews(tt, context);
            }
            if (t == typeof(DefaultController))
            {
                return null;
            }
            throw new WebException(string.Format("The action {0} don't have view file!!!", actionName));
        }

        private static Type GetScaffoldingType(Type t)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ControllerBase<>))
            {
                return t.GetGenericArguments()[0];
            }
            if (t.BaseType == typeof(object))
            {
                throw new WebException("System Error!");
            }
            return GetScaffoldingType(t.BaseType);
        }

        private static object CallAction(MethodBase mi, ControllerBase c, object[] ps)
        {
            object o = c.OnBeforeAction(mi.Name);
            if (o != null) return o;
            o = mi.Invoke(c, ps);
            if (o != null) return o;
            o = c.OnAfterAction(mi.Name);
            return o;
        }

        private static object ChangeType(string s, Type t)
        {
            if(t.IsValueType && string.IsNullOrEmpty(s))
            {
                return CommonHelper.GetEmptyValue(t);
            }
            return ClassHelper.ChangeType(s, t);
        }
    }
}
