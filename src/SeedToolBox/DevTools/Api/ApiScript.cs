using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jint;
using Jint.Runtime;
using Newtonsoft.Json;

namespace SeedToolBox.DevTools.Api;

sealed record ScriptInfo(string RequestName, int Iteration, int IterationCount, string EnvironmentName);

/// <summary>What the pm object in a script reaches: variables, the request, the response and the results. Called from the prelude.</summary>
public sealed class ScriptHost
{
    readonly ApiVariables _vars;
    readonly ApiRequest _request;
    readonly PreparedRequest? _prepared;
    readonly ApiResponse? _response;
    readonly ExecResult _result;
    internal readonly ScriptInfo Info;
    public string Phase { get; }

    internal ScriptHost(string phase, ApiVariables vars, ApiRequest request, PreparedRequest? prepared, ApiResponse? response, ScriptInfo info, ExecResult result)
    {
        Phase = phase;
        _vars = vars;
        _request = request;
        _prepared = prepared;
        _response = response;
        Info = info;
        _result = result;
    }

    public string RequestName => Info.RequestName;
    public int Iteration => Info.Iteration;
    public int IterationCount => Info.IterationCount;
    public string EnvironmentName => Info.EnvironmentName;

    // Variables. Scope is globals, collection, environment, data, local or all.

    public string? Get(string scope, string key) => scope switch
    {
        "all" => _vars.Get(key),
        "local" => _vars.Locals.TryGetValue(key, out var v) ? v : null,
        "data" => _vars.Data.TryGetValue(key, out var d) ? d : null,
        _ => _vars.Scope(scope)?.Active().LastOrDefault(k => k.Key == key)?.Value,
    };

    public void Set(string scope, string key, string value)
    {
        if (scope is "local" or "all") _vars.Locals[key] = value;
        else if (_vars.Scope(scope) is { } list) ApiVariables.Set(list, key, value);
    }

    public void Unset(string scope, string key)
    {
        if (scope is "local" or "all") _vars.Locals.Remove(key);
        else _vars.Scope(scope)?.RemoveAll(k => k.Key == key);
    }

    public void Clear(string scope)
    {
        if (scope is "local" or "all") _vars.Locals.Clear();
        else _vars.Scope(scope)?.Clear();
    }

    public string ToJson(string scope)
    {
        var map = new Dictionary<string, string>();
        IEnumerable<KeyValuePair<string, string>> pairs = scope switch
        {
            "local" => _vars.Locals,
            "data" => _vars.Data,
            _ => _vars.Scope(scope)?.Active().Select(k => new KeyValuePair<string, string>(k.Key, k.Value)) ?? Enumerable.Empty<KeyValuePair<string, string>>(),
        };
        foreach (var p in pairs) map[p.Key] = p.Value;
        return JsonConvert.SerializeObject(map);
    }

    public string Replace(string text) => _vars.Expand(text);

    // Request. Before sending the script edits the request; afterwards it sees what was sent.

    public string GetUrl() => _prepared?.Url ?? _request.Url;
    public void SetUrl(string url) { if (_prepared == null) _request.Url = url; }
    public string GetMethod() => _prepared?.Method ?? _request.Method;
    public void SetMethod(string method) { if (_prepared == null) _request.Method = method.ToUpperInvariant(); }

    public string HeadersJson() => JsonConvert.SerializeObject(_prepared != null
        ? _prepared.Headers.Select(h => new { key = h.Key, value = h.Value })
        : _request.Headers.Active().Select(h => new { key = h.Key, value = h.Value }));

    public void UpsertHeader(string key, string value)
    {
        if (_prepared != null) return;
        var row = _request.Headers.LastOrDefault(h => h.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (row == null) _request.Headers.Add(new KeyValue { Key = key, Value = value });
        else { row.Value = value; row.Enabled = true; }
    }

    public void AddHeader(string key, string value) { if (_prepared == null) _request.Headers.Add(new KeyValue { Key = key, Value = value }); }
    public void RemoveHeader(string key) { if (_prepared == null) _request.Headers.RemoveAll(h => h.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); }

    public string GetBody() => _prepared?.BodyText ?? (_request.Body.Mode == BodyModes.Raw ? _request.Body.Raw : "");
    public void SetBody(string raw)
    {
        if (_prepared != null) return;
        _request.Body.Mode = BodyModes.Raw;
        _request.Body.Raw = raw;
    }

    // Response

    public bool HasResponse => _response != null;
    public int Code => _response?.Status ?? 0;
    public string Reason => _response?.Reason ?? "";
    public double Time => _response?.Milliseconds ?? 0;
    public int Size => _response?.Bytes.Length ?? 0;
    public string Text() => _response?.Text ?? "";
    public string ResponseHeadersJson() => JsonConvert.SerializeObject(_response?.Headers.Select(h => new { key = h.Key, value = h.Value }) ?? Enumerable.Empty<object>());
    public string CookiesJson() => JsonConvert.SerializeObject(_response?.Cookies.Select(c => new { name = c.Name, value = c.Value, domain = c.Domain, path = c.Path }) ?? Enumerable.Empty<object>());

    // Results

    public void Log(string text) => _result.Console.Add(text);
    public void Test(string name, bool passed, string error) => _result.Tests.Add(new TestResult { Name = name, Passed = passed, Error = error });
    public void SetNext(string? name) => _result.NextRequest = name ?? "";
    public void Skip() => _result.Skipped = true;
    internal void Fail(string message)
    {
        _result.Console.Add("脚本出错：" + message);
        if (Phase == "prerequest") _result.Error = "前置脚本出错：" + message;
    }
}

/// <summary>Runs pre-request and test scripts with a Postman-like pm object.</summary>
static class ApiScript
{
    /// <summary>False when the script failed; the error goes to the console and, in tests, a failed test. Throws when <paramref name="cancel"/> stops it.</summary>
    public static bool Run(string script, ScriptHost host, CancellationToken cancel = default)
    {
        try
        {
            var engine = new Engine(o => o
                .TimeoutInterval(TimeSpan.FromSeconds(10))
                .LimitMemory(128_000_000)
                .LimitRecursion(512)
                .Strict(false)
                .CancellationToken(cancel));
            engine.SetValue("__host", host);
            engine.Execute(Prelude, "prelude.js");
            engine.Execute(script, host.Phase + ".js");
            engine.Execute("__finish();", "finish.js");
            return true;
        }
        catch (JavaScriptException ex)
        {
            Report(host, ex.Message + (ex.Location.Start.Line > 0 ? $"（第 {ex.Location.Start.Line} 行）" : ""));
        }
        catch (ExecutionCanceledException)
        {
            throw new OperationCanceledException(cancel);
        }
        catch (TimeoutException)
        {
            Report(host, "运行超过 10 秒，已停止");
        }
        catch (Exception ex) when (ex is JintException or Acornima.ParseErrorException or InvalidOperationException or ArgumentException)
        {
            Report(host, ex.Message);
        }
        return false;
    }

    static void Report(ScriptHost host, string message)
    {
        host.Fail(message);
        if (host.Phase == "test") host.Test("测试脚本出错", false, message);
    }

    /// <summary>The pm object, chai-style expect, console and the old postman.* / tests[] forms.</summary>
    const string Prelude = @"
var __h = __host;
function __fmt(v) { if (typeof v === 'string') return v; if (v === undefined) return 'undefined'; try { return JSON.stringify(v); } catch (e) { return String(v); } }
function __str(v) { return typeof v === 'string' ? v : __fmt(v); }
var console = { log: function () { __h.Log(Array.prototype.map.call(arguments, __fmt).join(' ')); } };
console.info = console.warn = console.error = console.debug = console.log;

function __scope(name) {
  return {
    get: function (k) { var v = __h.Get(name, String(k)); return v === null ? undefined : v; },
    set: function (k, v) { __h.Set(name, String(k), __str(v)); },
    unset: function (k) { __h.Unset(name, String(k)); },
    has: function (k) { return __h.Get(name, String(k)) !== null; },
    clear: function () { __h.Clear(name); },
    replaceIn: function (s) { return __h.Replace(String(s)); },
    toObject: function () { return JSON.parse(__h.ToJson(name)); }
  };
}

function __headerList(json, editable) {
  var list = JSON.parse(json);
  var find = function (k) { k = String(k).toLowerCase(); for (var i = list.length - 1; i >= 0; i--) if (list[i].key.toLowerCase() === k) return list[i]; return null; };
  return {
    get: function (k) { var h = find(k); return h ? h.value : undefined; },
    has: function (k) { return find(k) !== null; },
    toObject: function () { var o = {}; list.forEach(function (h) { o[h.key] = h.value; }); return o; },
    all: function () { return list; },
    each: function (f) { list.forEach(f); },
    count: function () { return list.length; },
    add: function (h) { if (editable) { __h.AddHeader(String(h.key), __str(h.value)); list.push({ key: h.key, value: __str(h.value) }); } },
    upsert: function (h) { if (editable) { __h.UpsertHeader(String(h.key), __str(h.value)); var o = find(h.key); if (o) o.value = __str(h.value); else list.push({ key: h.key, value: __str(h.value) }); } },
    remove: function (k) { if (editable) { __h.RemoveHeader(String(k)); k = String(k).toLowerCase(); list = list.filter(function (h) { return h.key.toLowerCase() !== k; }); } }
  };
}

function __deq(a, b) {
  if (a === b) return true;
  if (a === null || b === null || typeof a !== 'object' || typeof b !== 'object') return a !== a && b !== b;
  if (Array.isArray(a) !== Array.isArray(b)) return false;
  var ka = Object.keys(a), kb = Object.keys(b);
  if (ka.length !== kb.length) return false;
  for (var i = 0; i < ka.length; i++) if (!Object.prototype.hasOwnProperty.call(b, ka[i]) || !__deq(a[ka[i]], b[ka[i]])) return false;
  return true;
}
function __type(v) { if (v === null) return 'null'; if (Array.isArray(v)) return 'array'; if (v instanceof RegExp) return 'regexp'; if (v instanceof Date) return 'date'; return typeof v; }
function __show(v) { return typeof v === 'string' ? '""' + v + '""' : __fmt(v); }

function Assertion(obj, flags) { this._o = obj; this._not = false; this._deep = false; this._msg = flags || ''; }
var __A = Assertion.prototype;
__A._ok = function (ok, msg, notMsg) {
  if (this._not ? ok : !ok) throw new Error((this._msg ? this._msg + ': ' : '') + (this._not ? notMsg : msg));
  return this;
};
['to', 'be', 'been', 'is', 'that', 'which', 'and', 'has', 'have', 'with', 'at', 'of', 'same', 'but', 'does', 'any', 'all', 'own', 'still', 'also'].forEach(function (w) {
  Object.defineProperty(__A, w, { get: function () { return this; } });
});
Object.defineProperty(__A, 'not', { get: function () { this._not = !this._not; return this; } });
Object.defineProperty(__A, 'deep', { get: function () { this._deep = true; return this; } });
function __prop(name, test, msg, notMsg) { Object.defineProperty(__A, name, { get: function () { return this._ok(test.call(this, this._o), msg.call(this), notMsg.call(this)); } }); }
__prop('ok', function (o) { return o && o.__isResponse ? o.code >= 200 && o.code < 300 : !!o; }, function () { return '期望 ' + __show(this._o && this._o.__isResponse ? this._o.code : this._o) + ' 为真 / 2xx'; }, function () { return '期望不为真'; });
__prop('true', function (o) { return o === true; }, function () { return '期望 ' + __show(this._o) + ' 为 true'; }, function () { return '期望不为 true'; });
__prop('false', function (o) { return o === false; }, function () { return '期望 ' + __show(this._o) + ' 为 false'; }, function () { return '期望不为 false'; });
__prop('null', function (o) { return o === null; }, function () { return '期望 ' + __show(this._o) + ' 为 null'; }, function () { return '期望不为 null'; });
__prop('undefined', function (o) { return o === undefined; }, function () { return '期望 ' + __show(this._o) + ' 为 undefined'; }, function () { return '期望不为 undefined'; });
__prop('NaN', function (o) { return o !== o; }, function () { return '期望为 NaN'; }, function () { return '期望不为 NaN'; });
__prop('exist', function (o) { return o !== null && o !== undefined; }, function () { return '期望存在'; }, function () { return '期望不存在，实际是 ' + __show(this._o); });
__prop('empty', function (o) { return o === '' || (Array.isArray(o) && o.length === 0) || (o && typeof o === 'object' && Object.keys(o).length === 0); }, function () { return '期望 ' + __show(this._o) + ' 为空'; }, function () { return '期望不为空'; });
__prop('json', function (o) { try { JSON.parse(o.__isResponse ? o.text() : o); return true; } catch (e) { return false; } }, function () { return '期望响应是 JSON'; }, function () { return '期望响应不是 JSON'; });
function __range(name, lo, hi) { __prop(name, function (o) { return o.code >= lo && o.code <= hi; }, function () { return '期望状态码在 ' + lo + '-' + hi + '，实际 ' + this._o.code; }, function () { return '期望状态码不在 ' + lo + '-' + hi; }); }
__range('success', 200, 299); __range('info', 100, 199); __range('redirection', 300, 399);
__range('clientError', 400, 499); __range('serverError', 500, 599); __range('error', 400, 599);
__range('notFound', 404, 404); __range('unauthorized', 401, 401); __range('forbidden', 403, 403); __range('badRequest', 400, 400);
__A.equal = __A.equals = __A.eq = function (v) {
  var ok = this._deep ? __deq(this._o, v) : this._o === v;
  return this._ok(ok, '期望 ' + __show(this._o) + ' 等于 ' + __show(v), '期望 ' + __show(this._o) + ' 不等于 ' + __show(v));
};
__A.eql = function (v) { return this._ok(__deq(this._o, v), '期望 ' + __show(this._o) + ' 深度等于 ' + __show(v), '期望不深度等于 ' + __show(v)); };
__A.a = __A.an = function (t) { t = String(t).toLowerCase(); return this._ok(__type(this._o) === t, '期望类型是 ' + t + '，实际是 ' + __type(this._o), '期望类型不是 ' + t); };
__A.include = __A.includes = __A.contain = __A.contains = function (v) {
  var o = this._o, ok;
  if (typeof o === 'string') ok = o.indexOf(v) >= 0;
  else if (Array.isArray(o)) ok = o.some(function (x) { return __deq(x, v); });
  else if (o && typeof o === 'object' && v && typeof v === 'object') ok = Object.keys(v).every(function (k) { return __deq(o[k], v[k]); });
  else ok = false;
  return this._ok(ok, '期望 ' + __show(o) + ' 包含 ' + __show(v), '期望 ' + __show(o) + ' 不包含 ' + __show(v));
};
__A.property = function (name, value) {
  var o = this._o, has = o !== null && o !== undefined && (typeof o === 'object' ? name in o : o[name] !== undefined);
  if (arguments.length < 2 || !has) return this._ok(has, '期望有属性 ' + name, '期望没有属性 ' + name);
  var next = new Assertion(o[name]); next._not = this._not;
  this._ok(o[name] === value || __deq(o[name], value), '期望属性 ' + name + ' 为 ' + __show(value) + '，实际是 ' + __show(o[name]), '期望属性 ' + name + ' 不为 ' + __show(value));
  return next;
};
__A.lengthOf = function (n) { var l = this._o == null ? undefined : this._o.length; return this._ok(l === n, '期望长度 ' + n + '，实际 ' + l, '期望长度不是 ' + n); };
__A.above = __A.gt = __A.greaterThan = function (n) { return this._ok(this._o > n, '期望 ' + __show(this._o) + ' 大于 ' + n, '期望不大于 ' + n); };
__A.below = __A.lt = __A.lessThan = function (n) { return this._ok(this._o < n, '期望 ' + __show(this._o) + ' 小于 ' + n, '期望不小于 ' + n); };
__A.least = __A.gte = function (n) { return this._ok(this._o >= n, '期望 ' + __show(this._o) + ' 至少 ' + n, '期望小于 ' + n); };
__A.most = __A.lte = function (n) { return this._ok(this._o <= n, '期望 ' + __show(this._o) + ' 至多 ' + n, '期望大于 ' + n); };
__A.within = function (lo, hi) { return this._ok(this._o >= lo && this._o <= hi, '期望 ' + __show(this._o) + ' 在 ' + lo + ' 到 ' + hi + ' 之间', '期望不在 ' + lo + ' 到 ' + hi + ' 之间'); };
__A.match = function (re) { return this._ok(re.test(this._o), '期望 ' + __show(this._o) + ' 匹配 ' + re, '期望不匹配 ' + re); };
__A.oneOf = function (list) { var o = this._o; return this._ok(list.some(function (x) { return __deq(x, o); }), '期望 ' + __show(o) + ' 是 ' + __fmt(list) + ' 之一', '期望不是 ' + __fmt(list) + ' 之一'); };
__A.keys = function () { var want = Array.isArray(arguments[0]) ? arguments[0] : Array.prototype.slice.call(arguments), o = this._o; return this._ok(want.every(function (k) { return o && k in o; }), '期望有键 ' + want.join(', '), '期望没有键 ' + want.join(', ')); };
__A.status = function (s) {
  var o = this._o, ok = typeof s === 'number' ? o.code === s : o.status === s;
  return this._ok(ok, '期望状态 ' + s + '，实际 ' + o.code + ' ' + o.status, '期望状态不是 ' + s);
};
__A.header = function (k, v) {
  var has = this._o.headers.has(k);
  if (arguments.length < 2) return this._ok(has, '期望有响应头 ' + k, '期望没有响应头 ' + k);
  var actual = this._o.headers.get(k);
  return this._ok(actual === v, '期望响应头 ' + k + ' 为 ' + __show(v) + '，实际 ' + __show(actual), '期望响应头 ' + k + ' 不为 ' + __show(v));
};
__A.body = function (s) { var t = this._o.text(); return this._ok(arguments.length ? t === s : t.length > 0, '期望响应体 ' + (arguments.length ? '为 ' + __show(s) : '不为空'), '期望响应体不符'); };
__A.jsonBody = function (path, value) {
  var cur; try { cur = this._o.json(); } catch (e) { return this._ok(false, '响应不是 JSON', ''); }
  if (!arguments.length) return this._ok(true, '', '期望响应不是 JSON');
  var parts = String(path).split('.'); for (var i = 0; i < parts.length && cur !== undefined && cur !== null; i++) cur = cur[parts[i]];
  if (arguments.length < 2) return this._ok(cur !== undefined, '期望 JSON 里有 ' + path, '期望 JSON 里没有 ' + path);
  return this._ok(__deq(cur, value), '期望 ' + path + ' 为 ' + __show(value) + '，实际 ' + __show(cur), '期望 ' + path + ' 不为 ' + __show(value));
};
function __expect(v, msg) { return new Assertion(v, msg); }
__expect.fail = function (msg) { throw new Error(msg || '失败'); };

var __tests = [];
var pm = {
  environment: __scope('environment'),
  collectionVariables: __scope('collection'),
  globals: __scope('globals'),
  iterationData: __scope('data'),
  variables: {
    get: function (k) { var v = __h.Get('all', String(k)); return v === null ? undefined : v; },
    set: function (k, v) { __h.Set('local', String(k), __str(v)); },
    unset: function (k) { __h.Unset('local', String(k)); },
    has: function (k) { return __h.Get('all', String(k)) !== null; },
    replaceIn: function (s) { return __h.Replace(String(s)); },
    toObject: function () { return JSON.parse(__h.ToJson('local')); }
  },
  info: { requestName: __h.RequestName, iteration: __h.Iteration, iterationCount: __h.IterationCount, eventName: __h.Phase },
  expect: __expect,
  test: function (name, fn) {
    try { fn(); __h.Test(String(name), true, ''); }
    catch (e) { __h.Test(String(name), false, String(e && e.message !== undefined ? e.message : e)); }
  },
  execution: {
    setNextRequest: function (n) { __h.SetNext(n === null || n === undefined ? null : String(n)); },
    skipRequest: function () { __h.Skip(); }
  }
};
pm.environment.name = __h.EnvironmentName;
pm.test.skip = function (name) { __h.Log('跳过测试：' + name); };
pm.setNextRequest = pm.execution.setNextRequest;

var __req = {
  get method() { return __h.GetMethod(); }, set method(v) { __h.SetMethod(String(v)); },
  headers: __headerList(__h.HeadersJson(), __h.Phase === 'prerequest'),
  body: { get raw() { return __h.GetBody(); }, set raw(v) { __h.SetBody(__str(v)); }, mode: 'raw', update: function (v) { __h.SetBody(typeof v === 'string' ? v : (v && v.raw !== undefined ? __str(v.raw) : __str(v))); } },
  addHeader: function (h) { this.headers.add(h); },
  removeHeader: function (k) { this.headers.remove(k); }
};
Object.defineProperty(__req, 'url', {
  get: function () { var u = __h.GetUrl(); return { toString: function () { return __h.GetUrl(); }, raw: u, getHost: function () { var m = /^[a-z]+:\/\/([^\/:?#]+)/i.exec(__h.GetUrl()); return m ? m[1] : ''; }, getPath: function () { var m = /^[a-z]+:\/\/[^\/?#]+([^?#]*)/i.exec(__h.GetUrl()); return m && m[1] ? m[1] : '/'; } }; },
  set: function (v) { __h.SetUrl(String(v)); }
});
pm.request = __req;

if (__h.HasResponse) {
  var __text = __h.Text();
  pm.response = {
    __isResponse: true,
    code: __h.Code, status: __h.Reason, responseTime: __h.Time, responseSize: __h.Size,
    headers: __headerList(__h.ResponseHeadersJson(), false),
    text: function () { return __text; },
    json: function () { return JSON.parse(__text); }
  };
  Object.defineProperty(pm.response, 'to', { get: function () { return new Assertion(pm.response); } });
  var __cookies = JSON.parse(__h.CookiesJson());
  pm.cookies = {
    get: function (n) { for (var i = 0; i < __cookies.length; i++) if (__cookies[i].name === n) return __cookies[i].value; return undefined; },
    has: function (n) { return __cookies.some(function (c) { return c.name === n; }); },
    toObject: function () { var o = {}; __cookies.forEach(function (c) { o[c.name] = c.value; }); return o; }
  };
  var responseBody = __text, responseCode = { code: __h.Code, name: __h.Reason, detail: __h.Reason }, responseTime = __h.Time;
  var responseHeaders = pm.response.headers.toObject();
}

var tests = {};
var postman = {
  setEnvironmentVariable: function (k, v) { pm.environment.set(k, v); },
  getEnvironmentVariable: function (k) { return pm.environment.get(k); },
  clearEnvironmentVariable: function (k) { pm.environment.unset(k); },
  setGlobalVariable: function (k, v) { pm.globals.set(k, v); },
  getGlobalVariable: function (k) { return pm.globals.get(k); },
  clearGlobalVariable: function (k) { pm.globals.unset(k); },
  setNextRequest: function (n) { pm.execution.setNextRequest(n); }
};
var environment = pm.environment.toObject(), globals = pm.globals.toObject();
function __finish() { for (var k in tests) __h.Test(k, !!tests[k], tests[k] ? '' : '结果为假'); }
";
}
