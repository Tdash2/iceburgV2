#include <SPI.h>
#include <Ethernet.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include <Wire.h>
#include <Adafruit_NeoPixel.h>

// ================= LED =================
Adafruit_NeoPixel pixels(1, 38, NEO_GRB + NEO_KHZ800);

// ================= RELAY =================
#define SDA_PIN 42
#define SCL_PIN 41
#define TCA9554_ADDR 0x20
#define REG_OUTPUT 0x01
#define REG_CONFIG 0x03

uint8_t relayState = 0xFF;

// ================= ETHERNET =================
#define W5500_CS 16
#define SPI_SCK  15
#define SPI_MISO 14
#define SPI_MOSI 13

byte mac[6];

EthernetServer webServer(80);
EthernetServer rossServer(1000);
EthernetClient rossClient;

// ================= CONFIG =================
Preferences prefs;

char primaryHost[64]   = "iceburg";
char secondaryHost[64] = "iceburg-backup";

int clientID = 1;
const int serverPort = 80;

bool useStatic = false;

IPAddress localIP(192,168,1,200);
IPAddress gateway(192,168,1,1);
IPAddress subnet(255,255,255,0);
IPAddress dns(8,8,8,8);

// ================= TALLY =================
#define MAX_INPUTS 128
#define TSL_PACKET 18

bool lastTally[MAX_INPUTS];
int inputCount = 0;

// ================= CONSOLE =================
#define CONSOLE_SIZE 4096
String consoleBuffer = "";

void logConsole(String msg)
{
  Serial.println(msg);
  consoleBuffer += msg + "\n";

  if(consoleBuffer.length() > CONSOLE_SIZE)
    consoleBuffer.remove(0, consoleBuffer.length() - CONSOLE_SIZE);
}

// ================= LED =================
void setPixel(uint8_t r,uint8_t g,uint8_t b)
{
  pixels.setPixelColor(0,pixels.Color(r,g,b));
  pixels.show();
}

// ================= RELAY =================
void writeTCA(uint8_t reg,uint8_t val)
{
  Wire.beginTransmission(TCA9554_ADDR);
  Wire.write(reg);
  Wire.write(val);
  Wire.endTransmission();
}

void setRelay(uint8_t ch, bool on)
{
  if (ch > 7) return;

  if (on) relayState |= (1 << ch);
  else relayState &= ~(1 << ch);

  writeTCA(REG_OUTPUT, relayState);
}

// ================= CONFIG =================
void loadConfig()
{
  prefs.begin("tally",true);

  prefs.getString("primary", primaryHost, sizeof(primaryHost));
  prefs.getString("secondary", secondaryHost, sizeof(secondaryHost));

  clientID = prefs.getInt("id",1);
  useStatic = prefs.getBool("static", false);

  localIP.fromString(prefs.getString("ip","192.168.1.200"));
  gateway.fromString(prefs.getString("gw","192.168.1.1"));
  subnet.fromString(prefs.getString("sub","255.255.255.0"));
  dns.fromString(prefs.getString("dns","8.8.8.8"));

  prefs.end();
}

void saveConfig()
{
  prefs.begin("tally",false);

  prefs.putString("primary", primaryHost);
  prefs.putString("secondary", secondaryHost);

  prefs.putInt("id",clientID);
  prefs.putBool("static", useStatic);

  prefs.putString("ip", localIP.toString());
  prefs.putString("gw", gateway.toString());
  prefs.putString("sub", subnet.toString());
  prefs.putString("dns", dns.toString());

  prefs.end();
}

// ================= SERVER FAILOVER =================
bool connectToServer(EthernetClient &c)
{
  if(c.connect(primaryHost, serverPort))
  {
    //logConsole("Connected PRIMARY");
    return true;
  }

  logConsole("Primary failed, trying secondary");

  if(c.connect(secondaryHost, serverPort))
  {
    logConsole("Connected SECONDARY");
    return true;
  }

  logConsole("Both servers FAILED");
  return false;
}

// ================= WEB SERVER =================
void handleWeb()
{
  EthernetClient c = webServer.available();
  if(!c) return;

  String req = c.readStringUntil('\r');
  c.read();

    if(req.startsWith("GET /console"))
  {
    c.println("HTTP/1.1 200 OK");
    c.println("Content-Type:text/plain");
    c.println();

    c.print(consoleBuffer);

    c.stop();
    return;
  }

if(req.startsWith("GET /debug"))
  {
    c.println("HTTP/1.1 200 OK");
    c.println("Content-Type:text/html\n");

    c.println("<html><body style='background:black;color:#0f0;font-family:monospace'>");
    c.println("<h3>ESP Ross Tally Console</h3>");
    c.println("<pre id='log'></pre>");

    c.println("<script>");
    c.println("async function update(){");
    c.println("let r=await fetch('/console');");
    c.println("let t=await r.text();");
    c.println("document.getElementById('log').textContent=t;");
    c.println("}");
    c.println("setInterval(update,500);");
    c.println("update();");
    c.println("</script>");

    c.println("</body></html>");

    c.stop();
    return;
  }

if(req.startsWith("GET /status"))
  {
    c.println("HTTP/1.1 200 OK");
    c.println("Content-Type:text/html\n");


    c.println("Device Type: Iceburg Tally Ross Switcher");
    c.println("<BR>");
    c.println("Tally ID: "+String(clientID));
    c.println("<BR>");
    c.println("Device IP: "+Ethernet.localIP().toString());


    c.stop();
    return;
  }

  // ===== SAVE =====
if (req.startsWith("POST /save")) {
  // Skip headers
  while (c.available() && c.readStringUntil('\n') != "\r");

  String body = c.readString();

  auto getVal = [&](const char* key) -> String {
    int p = body.indexOf(String(key) + "=");
    if (p < 0) return "";
    int e = body.indexOf('&', p);
    return body.substring(p + strlen(key) + 1,
                          e < 0 ? body.length() : e);
  };

  String v;

  v = getVal("primary");
  if (v.length()) {
    v.replace("+", " ");
    v.toCharArray(primaryHost, sizeof(primaryHost));
  }

  v = getVal("secondary");
  if (v.length()) {
    v.replace("+", " ");
    v.toCharArray(secondaryHost, sizeof(secondaryHost));
  }

  v = getVal("id");
  if (v.length()) clientID = v.toInt();

  // checkbox → exists or not
  useStatic = getVal("static").length() > 0;

  v = getVal("ip");  if (v.length()) localIP.fromString(v);
  v = getVal("gw");  if (v.length()) gateway.fromString(v);
  v = getVal("sub"); if (v.length()) subnet.fromString(v);
  v = getVal("dns"); if (v.length()) dns.fromString(v);

  saveConfig();

  delay(100);

String newIP = !useStatic ?
      "http://" + Ethernet.localIP().toString() :
      "http://" + localIP.toString();

    c.println("HTTP/1.1 200 OK");
    c.println("Content-Type: text/html");
    c.println("Connection: close\r\n");
    c.println("<html><body>");
    c.println("<h3>Settings saved. Restarting device...</h3>");
    c.println("<meta http-equiv='refresh' content='5; url="
                      + newIP + ":80'>");
    c.println("</body></html>");

    c.stop();
    ESP.restart();
  return;
}

  // ===== PAGE =====
  c.println("HTTP/1.1 200 OK\nContent-Type:text/html\n\n");

  c.println("<html><body style='background:#222;color:#fff'>");
  c.println("<head>");
  c.println("<title>Configure Iceburg Tally Ross Switcher</title>");
  c.println("<link rel='stylesheet' href='http://" +
                    String(primaryHost) +
                    "/depends/bootstrap.min.css'>");
  c.println("<script src='http://" +
                    String(primaryHost) +
                    "/depends/jquery.min.js'></script>");
  c.println("<script src='http://" +
                    String(primaryHost) +
                    "/depends/bootstrap.min.js'></script>");
  c.println("<style>body { background-color: #232323; color: #FFF; }</style>");
  c.println("</head>");


  c.println("<div class='container'>");
  c.println("<div class='py-5 text-center'>");
  c.println("<h2>Tally Config</h2>");
  c.println("<p>Connect a Ross switcher tally output to TCP port 1000. The input to this unit are from the Ross Switcher. Outputs are from ICEBURG Tally and connected to the Realys on the device.</p>");

  c.println("</div>");

  c.println("<form method='POST' action='/save'>");

  c.println("<div class='form-group'>");
  c.println("<label>Primary Server</label>");
  c.println("<input name='primary' class='form-control' value='"+String(primaryHost)+"'>");
  c.println("</div>");


  c.println("<div class='form-group'>");
  c.println("<label>Secondary Server</label>");
  c.println("<input name='secondary' class='form-control' value='"+String(secondaryHost)+"'>");
  c.println("</div>");

  c.println("<div class='form-group'>");
  c.println("<label>Client ID</label>");
  c.println("Client ID: <input name='id' class='form-control' value='"+String(clientID)+"'>");
  c.println("</div>");

  c.println("<br>");

   c.println("<div class='form-group'>");
  c.println("<label>Use Static IP</label>"); 
  c.println("<input type='checkbox' class='form-control' style=\"width: auto;\" name='static' "+String(useStatic?"checked":"")+">");
  c.println("</div>");

  c.println("<div class='form-group'>");
  c.println("<label>Static IP</label>");
  c.println("<input name='ip' class='form-control' value='"+localIP.toString()+"'>");
c.println("</div>");

  c.println("<div class='form-group'>");
  c.println("<label>Default Gateway</label>");
  c.println("<input name='gw' class='form-control' value='"+gateway.toString()+"'>");
c.println("</div>");

  c.println("<div class='form-group'>");
  c.println("<label>Subnet Mask</label>");
  c.println("<input name='sub' class='form-control' value='"+subnet.toString()+"'>");
c.println("</div>");

  c.println("<div class='form-group'>");
  c.println("<label>DNS Server</label>");
  c.println("<input name='dns' class='form-control' value='"+dns.toString()+"'>");
c.println("</div>");

  c.println("<button type='submit' class='btn btn-primary'>Save</button>");
  c.println(" <a class='btn btn-primary' href='http://" +String(primaryHost) +"'>Iceburg Server Login</a>");
  c.println(" <a class='btn btn-primary' href='/debug'>Debug Tally Data</a>");
  c.println("</form></div></body></html>");

  c.stop();
}

// ================= ROSS =================
void readRoss()
{
  if(!rossClient || !rossClient.connected())
  {
    setPixel(0,25,0);
    rossClient = rossServer.available();
    if(!rossClient) return;

    logConsole("Ross connected");
    
  }

  while(rossClient.available() >= TSL_PACKET)
  {
    uint8_t header = rossClient.read();
    uint8_t ctrl   = rossClient.read();

    char text[17];
    for(int i=0;i<16;i++) text[i]=rossClient.read();
    text[16]=0;

    int addr = header - 0x80;
    bool program = ctrl & 0x02;

    if(addr>=0 && addr<MAX_INPUTS)
    {
      lastTally[addr] = program;
      if(addr+1 > inputCount) inputCount = addr+1;
    }
    logConsole("Input "+String(addr)+" UMD:"+String(text)+" PGM Tally Status: "+String(program));
    setPixel(25,0,0);
  }
   
}

// ================= SEND =================
void sendToServer()
{
  EthernetClient c;

  if(!connectToServer(c))
  {
    setPixel(0,25,0);
    return;
  }

  String url="/tally/settallystatus.php?id="+String(clientID)+"&IP="+Ethernet.localIP().toString();

  for(int i=0;i<25 && i<inputCount;i++)
    url += "&ch"+String(i)+"="+String(lastTally[i]?"1":"0");

  c.print("GET "+url+" HTTP/1.0\r\n\r\n");

  while(c.connected())
    while(c.available()) c.read();

  c.stop();
  setPixel(25,0,0);
}

// ================= POLL =================
void pollServer()
{
  EthernetClient c;

  if(!connectToServer(c))
  {
    setPixel(0,25,0);
    return;
  }

  c.print("GET /tally/gettallystatus.php?id="+String(clientID)+"&IP="+Ethernet.localIP().toString()+" HTTP/1.0\r\n\r\n");

  while(c.connected())
  {
    String line=c.readStringUntil('\n');
    if(line=="\r") break;
  }

  StaticJsonDocument<512> doc;

  if(deserializeJson(doc,c))
  {
    logConsole("JSON parse error");
    c.stop();
    return;
  }

  JsonObject outputs = doc["outputs"];

  for(int i=0;i<8;i++)
    setRelay(i, outputs[String(i+1)] | false);

  c.stop();
}

// ================= SETUP =================
void setup()
{
Serial.begin(115200);
unsigned long start = millis();
while(!Serial && millis() - start < 3000) {
  delay(10);
}
Serial.println("Booting...");

  pixels.begin();
  pixels.show();
  loadConfig();

  Wire.begin(SDA_PIN,SCL_PIN);
  writeTCA(REG_CONFIG,0x00);
  writeTCA(REG_OUTPUT,relayState);

  

  uint64_t chip = ESP.getEfuseMac();

  mac[0]=0x02;
  mac[1]=chip>>40;
  mac[2]=chip>>32;
  mac[3]=chip>>24;
  mac[4]=chip>>16;
  mac[5]=chip>>8;

  SPI.begin(SPI_SCK,SPI_MISO,SPI_MOSI);
  Ethernet.init(W5500_CS);

  if(useStatic)
  {
    Ethernet.begin(mac, localIP, dns, gateway, subnet);
    logConsole("Static IP mode");
  }
  else
  {
if(Ethernet.begin(mac)==0)
{
  Serial.println("DHCP failed");  // direct print
  delay(1000);                    // give time to flush
  while(true);
}
  }

  logConsole("IP: "+Ethernet.localIP().toString());
  Serial.print("IP: "+Ethernet.localIP().toString());
  delay(1000);
  webServer.begin();
  rossServer.begin();

  setPixel(0,0,25);

}

// ================= LOOP =================
unsigned long lastPoll=0;

void loop()
{
  handleWeb();
  readRoss();

  if(millis()-lastPoll > 150)
  {
    lastPoll = millis();
    sendToServer();
    pollServer();
  }

}