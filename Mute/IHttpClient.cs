﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Mute
{
    public interface IHttpClient
    {
        Task<HttpResponseMessage> GetAsync(string uri);
    }
}
