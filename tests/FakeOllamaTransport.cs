using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

sealed class FakeOllamaTransport : HttpMessageHandler {
    public string Tags = "{\"models\":[{\"name\":\"blink-test:local\"}]}";
    public string Chat, Style, LastChatBody, LastInterpretation;
    public int ChatRequests;
    public bool Offline;

    public void Attach() {
        var field = typeof(Language).GetField("Client",BindingFlags.Static|BindingFlags.NonPublic);
        var real = (HttpClient)field.GetValue(null);
        field.SetValue(null,new HttpClient(this){ BaseAddress=real.BaseAddress, Timeout=real.Timeout });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) {
        if(Offline) throw new HttpRequestException("Offline for test");
        if(request.RequestUri.Host != "127.0.0.1" || request.RequestUri.Port != 11434)
            throw new Exception("Unexpected destination");
        string reply;
        if(request.Method==HttpMethod.Get && request.RequestUri.AbsolutePath=="/api/tags") reply=Tags;
        else if(request.Method==HttpMethod.Post && request.RequestUri.AbsolutePath=="/api/chat") {
            ChatRequests++; LastChatBody=await request.Content.ReadAsStringAsync();
            bool styling=LastChatBody.Contains("Choose a short opening for this desktop pet");
            if(!styling) LastInterpretation=LastChatBody;
            reply=new JavaScriptSerializer().Serialize(new { model="blink-test:local",done=true,message=new {role="assistant",content=styling ? Style : Chat} });
        } else throw new Exception("Unknown Ollama route");
        return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(reply,Encoding.UTF8,"application/json")};
    }
}
