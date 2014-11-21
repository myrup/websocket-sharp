#region License
/*
 * HttpListenerAsyncResult.cs
 *
 * This code is derived from System.Net.ListenerAsyncResult.cs of Mono
 * (http://www.mono-project.com).
 *
 * The MIT License
 *
 * Copyright (c) 2005 Ximian, Inc. (http://www.ximian.com)
 * Copyright (c) 2012-2014 sta.blockhead
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in
 * all copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
 * THE SOFTWARE.
 */
#endregion

#region Authors
/*
 * Authors:
 * - Gonzalo Paniagua Javier <gonzalo@ximian.com>
 */
#endregion

using System;
using System.Security.Principal;
using System.Threading;

namespace WebSocketSharp.Net
{
  internal class HttpListenerAsyncResult : IAsyncResult
  {
    #region Private Fields

    private AsyncCallback       _callback;
    private bool                _completed;
    private HttpListenerContext _context;
    private Exception           _exception;
    private object              _state;
    private object              _sync;
    private bool                _syncCompleted;
    private ManualResetEvent    _waitHandle;

    #endregion

    #region Internal Fields

    internal bool EndCalled;
    internal bool InGet;

    #endregion

    #region Public Constructors

    public HttpListenerAsyncResult (AsyncCallback callback, object state)
    {
      _callback = callback;
      _state = state;
      _sync = new object ();
    }

    #endregion

    #region Public Properties

    public object AsyncState {
      get {
        return _state;
      }
    }

    public WaitHandle AsyncWaitHandle {
      get {
        lock (_sync)
          return _waitHandle ?? (_waitHandle = new ManualResetEvent (_completed));
      }
    }

    public bool CompletedSynchronously {
      get {
        return _syncCompleted;
      }
    }

    public bool IsCompleted {
      get {
        lock (_sync)
          return _completed;
      }
    }

    #endregion

    #region Private Methods

    private static bool authenticate (HttpListenerContext context)
    {
      var listener = context.Listener;
      var schm = listener.SelectAuthenticationScheme (context);
      if (schm == AuthenticationSchemes.Anonymous)
        return true;

      if (schm == AuthenticationSchemes.None) {
        context.Response.Close (HttpStatusCode.Forbidden);
        return false;
      }

      var req = context.Request;
      var realm = listener.Realm;
      var user = createUser (
        req.Headers["Authorization"], schm, realm, req.HttpMethod, listener.UserCredentialsFinder);

      if (user != null && user.Identity.IsAuthenticated) {
        context.User = user;
        req.IsAuthenticated = true;

        return true;
      }

      if (schm == AuthenticationSchemes.Basic)
        context.Response.CloseWithAuthChallenge (
          AuthenticationChallenge.CreateBasicChallenge (realm).ToBasicString ());
      else if (schm == AuthenticationSchemes.Digest)
        context.Response.CloseWithAuthChallenge (
          AuthenticationChallenge.CreateDigestChallenge (realm).ToDigestString ());
      else
        context.Response.Close (HttpStatusCode.Forbidden);

      return false;
    }

    private static void complete (HttpListenerAsyncResult asyncResult)
    {
      asyncResult._completed = true;

      var waitHandle = asyncResult._waitHandle;
      if (waitHandle != null)
        waitHandle.Set ();

      var callback = asyncResult._callback;
      if (callback != null)
        ThreadPool.UnsafeQueueUserWorkItem (
          state => {
            try {
              callback (asyncResult);
            }
            catch {
            }
          },
          null);
    }

    private static IPrincipal createUser (
      string response,
      AuthenticationSchemes scheme,
      string realm,
      string method,
      Func<IIdentity, NetworkCredential> credentialsFinder)
    {
      if (response == null ||
          !response.StartsWith (scheme.ToString (), StringComparison.OrdinalIgnoreCase))
        return null;

      var res = AuthenticationResponse.Parse (response);
      if (res == null)
        return null;

      var id = res.ToIdentity ();
      if (id == null)
        return null;

      NetworkCredential cred = null;
      try {
        cred = credentialsFinder (id);
      }
      catch {
      }

      if (cred == null)
        return null;

      var valid = scheme == AuthenticationSchemes.Basic
                  ? ((HttpBasicIdentity) id).Password == cred.Password
                  : scheme == AuthenticationSchemes.Digest
                    ? ((HttpDigestIdentity) id).IsValid (cred.Password, realm, method, null)
                    : false;

      return valid
             ? new GenericPrincipal (id, cred.Roles)
             : null;
    }

    #endregion

    #region Internal Methods

    internal void Complete (Exception exception)
    {
      _exception = InGet && (exception is ObjectDisposedException)
                   ? new HttpListenerException (500, "Listener closed.")
                   : exception;

      lock (_sync)
        complete (this);
    }

    internal void Complete (HttpListenerContext context)
    {
      Complete (context, false);
    }

    internal void Complete (HttpListenerContext context, bool syncCompleted)
    {
      if (!authenticate (context)) {
        context.Listener.BeginGetContext (this);
        return;
      }

      _context = context;
      _syncCompleted = syncCompleted;

      lock (_sync)
        complete (this);
    }

    internal HttpListenerContext GetContext ()
    {
      if (_exception != null)
        throw _exception;

      return _context;
    }

    #endregion
  }
}
