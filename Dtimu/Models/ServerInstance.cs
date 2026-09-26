using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.UI.Popups;

namespace Dtimu.Models
{
    public class ServerInstance
    {

        public string IPAddress { get; set; }
        public Version Version { get; set; }
        public string HostName { get; set; }
        public DireInfo DireInfo { get; set; }
        public void RefreshList()
        {
            
            if (string.IsNullOrWhiteSpace(IPAddress))
            {
                throw new NullReferenceException("软粉你干什么吃的！");
            }
            
            

                var client = new HttpClient();
                var request = new HttpRequestMessage(HttpMethod.Get, $"http://{IPAddress}/api/Lists/All");
                var content = new MultipartFormDataContent();
                request.Content = content;
                var response = client.SendAsync(request).GetAwaiter().GetResult();
                response.EnsureSuccessStatusCode();
                var jT = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                DireInfo = DireInfoUtil.Load(jT);
            
        }
    }
}
