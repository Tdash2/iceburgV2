#include <SPI.h>
#include <Ethernet.h>
#include <ArduinoJson.h>
#include <Preferences.h>
#include "esp_system.h"
#include <Wire.h>
#include <Adafruit_NeoPixel.h>

HardwareSerial RS485Serial(1);

#define RS485_TX 17
#define RS485_RX 18


Adafruit_NeoPixel pixels(1, 38, NEO_GRB + NEO_KHZ800);

#define SDA_PIN 42
#define SCL_PIN 41
#define TCA9554_ADDR 0x20

#define REG_INPUT     0x00
#define REG_OUTPUT    0x01
#define REG_POLARITY  0x02
#define REG_CONFIG    0x03

uint8_t relayState = 0xFF;

#define W5500_CS 16
#define SPI_SCK  15
#define SPI_MISO 14
#define SPI_MOSI 13

enum ServerType {
    SERVER_PRIMARY,
    SERVER_SECONDARY
};

ServerType activeServer = SERVER_PRIMARY;

unsigned long lastPrimaryCheck = 0;

const unsigned long PRIMARY_RETRY_INTERVAL = 10000;

uint8_t primarySuccessCount = 0;

const uint8_t PRIMARY_SUCCESS_REQUIRED = 3;

bool serverConnected = false;

const int inputPins[8] = {4,5,6,7,8,9,10,11};
bool lastInputState[8];
bool lastSentState[8] = {0};
char umdLabels[32][17];
bool tslTallies[32];

unsigned long lastUMDUpdate = 0;

unsigned long lastTallyPoll = 0;
unsigned long lastUMDPoll   = 0;
unsigned long lastrecovery   = 0;

const uint16_t TALLY_INTERVAL = 100;   // 100 ms
const uint16_t UMD_INTERVAL   = 1000;  // 1 second
const uint16_t recovery_INTERVAL   = 1000;  // 1 second

byte mac[6];
EthernetClient client;
EthernetServer webServer(80);

bool useDHCP = false;
IPAddress staticIP(192,168,1,50);
IPAddress gateway(192,168,1,1);
IPAddress subnet(255,255,255,0);
IPAddress dns(8,8,8,8);

Preferences prefs;

/* ---------- SERVER CONFIG ---------- */
char primaryHost[64]   = "iceburg";
char secondaryHost[64] = "iceburg-backup";
char activeHost[64]    = "iceburg";
const int serverPort = 80;
/* ----------------------------------- */

String clientID = "";

unsigned long lastGetPoll = 0;
const unsigned long getInterval = 100;
void rs485EnableTX() {
#ifdef RS485_DE
  digitalWrite(RS485_DE, HIGH);
#endif
}

void rs485EnableRX() {
#ifdef RS485_DE
  digitalWrite(RS485_DE, LOW);
#endif
}


void updateAllTSL()
{
  for(int i=0;i<32;i++)
  {
    sendTSL(i+1, tslTallies[i], umdLabels[i]);
  }
}

void sendTSL(uint8_t address, bool tallyState, const char* label)
{
  uint8_t header = 0x80 + address;

  uint8_t ctrl = 0;

  if(tallyState)
    ctrl |= (1 << 1);   // tally1 ON (RED lamp)

  ctrl |= (3 << 4);     // full brightness

  char text[16];
  memset(text,' ',16);
  strncpy(text,label,16);

  

  RS485Serial.write(header);
  RS485Serial.write(ctrl);
  RS485Serial.write((uint8_t*)text,16);

  RS485Serial.flush();

  
}



void fetchUMD()
{
  if (!connectToServer()) return;

  client.print("GET /tally/getumd.php?id="+clientID+" HTTP/1.0\r\n\r\n");

  while (client.connected()) {
    String line = client.readStringUntil('\n');
    if (line == "\r") break;
  }

  StaticJsonDocument<2048> doc;

  if (deserializeJson(doc, client)) {
    client.stop();
    return;
  }

  JsonObject outputs = doc["outputs"];

  for(int i=0;i<32;i++)
  {
    String key = String(i+9);

    if(outputs.containsKey(key))
    {
      String label = outputs[key];
      label.toCharArray(umdLabels[i],17);
    }
  }

  client.stop();
}

/* ---------- I2C + RELAY ---------- */
void writeTCA(uint8_t reg, uint8_t value) {
  Wire.beginTransmission(TCA9554_ADDR);
  Wire.write(reg);
  Wire.write(value);
  Wire.endTransmission();
}

void setRelay(uint8_t relay, bool on) {
  if (relay > 7) return;
  if (!on) relayState &= ~(1 << relay);
  else relayState |= (1 << relay);
  writeTCA(REG_OUTPUT, relayState);
}
void setTallyOutput(uint8_t ch, bool state)
{
  uint8_t address = ch + 1;

  sendTSL(address, state, umdLabels[ch]);
}

/* ---------- LED ---------- */
void setPixelRed() {
  pixels.setPixelColor(0, pixels.Color(0,25,0));
  pixels.show();
}

void setPixelGreen() {
  pixels.setPixelColor(0, pixels.Color(25,0,0));
  pixels.show();
}

/* ---------- MAC ---------- */
void generateEthernetMAC(byte *mac) {
  uint64_t chipMac = ESP.getEfuseMac();
  mac[0] = 0x02;
  mac[1] = (chipMac >> 40) & 0xFF;
  mac[2] = (chipMac >> 32) & 0xFF;
  mac[3] = (chipMac >> 24) & 0xFF;
  mac[4] = (chipMac >> 16) & 0xFF;
  mac[5] = (chipMac >> 8) & 0xFF;
}

/* ---------- CONFIG LOAD ---------- */
void loadConfig() {
  prefs.begin("tally", true);

  prefs.getString("host1", primaryHost, sizeof(primaryHost));
  prefs.getString("host2", secondaryHost, sizeof(secondaryHost));
clientID = prefs.getString("id", "");
  useDHCP = prefs.getBool("dhcp", true);

  staticIP.fromString(prefs.getString("ip","192.168.1.50"));
  gateway.fromString(prefs.getString("gw","192.168.1.1"));
  subnet.fromString(prefs.getString("sn","255.255.255.0"));
  dns.fromString(prefs.getString("dns","8.8.8.8"));

  prefs.end();
}

/* ---------- CONFIG SAVE ---------- */
void saveConfig() {
  prefs.begin("tally", false);

  prefs.putString("host1", primaryHost);
  prefs.putString("host2", secondaryHost);
  prefs.putString("id", clientID);

  prefs.putBool("dhcp", useDHCP);
  prefs.putString("ip", staticIP.toString());
  prefs.putString("gw", gateway.toString());
  prefs.putString("sn", subnet.toString());
  prefs.putString("dns", dns.toString());

  prefs.end();
}

/* ---------- FAILOVER CONNECT ---------- */
bool connectToServer()
{
    client.stop();

    // PRIMARY MODE
    if (activeServer == SERVER_PRIMARY)
    {
        if (client.connect(primaryHost, serverPort))
        {
            strcpy(activeHost, primaryHost);
            setPixelGreen();
            serverConnected = true;
            return true;
        }

        Serial.println("PRIMARY FAILED - SWITCHING TO SECONDARY");

        activeServer = SERVER_SECONDARY;
        primarySuccessCount = 0;
    }

    // SECONDARY MODE
    if (activeServer == SERVER_SECONDARY)
    {
        if (client.connect(secondaryHost, serverPort))
        {
            strcpy(activeHost, secondaryHost);
            setPixelGreen();
            serverConnected = true;
            return true;
        }

        Serial.println("SECONDARY FAILED");

        serverConnected = false;
        setPixelRed();

        return false;
    }

    return false;
}

void checkPrimaryRecovery()
{
    if (activeServer != SERVER_SECONDARY)
        return;

    if (millis() - lastPrimaryCheck < PRIMARY_RETRY_INTERVAL)
        return;

    lastPrimaryCheck = millis();

    client.stop();

    Serial.println("Checking primary server...");

    if (client.connect(primaryHost, serverPort))
    {
        client.stop();

        primarySuccessCount++;

        Serial.print("Primary health check: ");
        Serial.println(primarySuccessCount);

        if (primarySuccessCount >= PRIMARY_SUCCESS_REQUIRED)
        {
            activeServer = SERVER_PRIMARY;

            strcpy(activeHost, primaryHost);

            primarySuccessCount = 0;

            Serial.println("PRIMARY RESTORED - FAILBACK COMPLETE");

            setPixelGreen();
        }
    }
    else
    {
        client.stop();

        primarySuccessCount = 0;

        Serial.println("Primary still offline");
    }
}

/* ---------- WEB UI ---------- */
void handleWeb() {
  EthernetClient webClient = webServer.available();
  if (!webClient) return;

  String req = webClient.readStringUntil('\r');
  webClient.read(); // \n

  // ---- POST /save ----
  if (req.startsWith("POST /save")) {
    while (webClient.available() &&
           webClient.readStringUntil('\n') != "\r");

    String body = webClient.readString();

    auto getVal = [&](const char* key) -> String {
      int p = body.indexOf(String(key) + "=");
      if (p < 0) return "";
      int e = body.indexOf('&', p);
      return body.substring(p + strlen(key) + 1,
                             e < 0 ? body.length() : e);
    };

    String v;

    v = getVal("host1");
    if (v.length()) {
      v.replace("+", " ");
      v.toCharArray(primaryHost, sizeof(primaryHost));
    }

    v = getVal("host2");
    if (v.length()) {
      v.replace("+", " ");
      v.toCharArray(secondaryHost, sizeof(secondaryHost));
    }

    v = getVal("id");
if (v.length()) clientID = v;

    v = getVal("dhcp");
    if (v.length()) useDHCP = v.toInt();

    v = getVal("ip");  if (v.length()) staticIP.fromString(v);
    v = getVal("gw");  if (v.length()) gateway.fromString(v);
    v = getVal("sn");  if (v.length()) subnet.fromString(v);
    v = getVal("dns"); if (v.length()) dns.fromString(v);

    saveConfig();
    delay(100);

    String newIP = useDHCP ?
      "http://" + Ethernet.localIP().toString() :
      "http://" + staticIP.toString();

    webClient.println("HTTP/1.1 200 OK");
    webClient.println("Content-Type: text/html");
    webClient.println("Connection: close\r\n");
    webClient.println("<html><body>");
    webClient.println("<h3>Settings saved. Restarting device...</h3>");
    webClient.println("<meta http-equiv='refresh' content='5; url="
                      + newIP + ":80'>");
    webClient.println("</body></html>");

    webClient.stop();
    ESP.restart();
  }

  // ---- STATUS PAGE ----
  if (req.startsWith("GET /status")) {
    webClient.println("HTTP/1.1 200 OK");
    webClient.println("Content-Type: text/html");
    webClient.println("Connection: close\r\n");

    webClient.println("Device Type: Iceburg Tally 8x8 <br>");
    webClient.println("Tally ID: " + clientID + "<br>");
    webClient.println("IP: " + Ethernet.localIP().toString() + "<br>");
    webClient.println("Active Server: " + String(activeHost) + "<br>");
    webClient.println("Mode: " + String(useDHCP ? "DHCP" : "Static"));

    webClient.stop();
    return;
  }

  // ---- CONFIG PAGE ----
  webClient.println("HTTP/1.1 200 OK");
  webClient.println("Content-Type: text/html");
  webClient.println("Connection: close\r\n");

  webClient.println("<html>");
  webClient.println("<head>");
  webClient.println("<title>Configure Iceburg Tally 8x8</title>");
  webClient.println("<link rel='stylesheet' href='http://" +
                    String(primaryHost) +
                    "/depends/bootstrap.min.css'>");
  webClient.println("<script src='http://" +
                    String(primaryHost) +
                    "/depends/jquery.min.js'></script>");
  webClient.println("<script src='http://" +
                    String(primaryHost) +
                    "/depends/bootstrap.min.js'></script>");
  webClient.println("<style>body { background-color: #232323; color: #FFF; }</style>");
  webClient.println("</head>");

  webClient.println("<body>");
  webClient.println("<div class='container'>");
  webClient.println("<div class='py-5 text-center'>");
  webClient.println("<h2>Configure Iceburg Tally 8x8</h2>");
  webClient.println("</div>");

  webClient.println("<form method='POST' action='/save'>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>Primary Server</label>");
  webClient.println("<input type='text' class='form-control' "
                    "name='host1' value='" +
                    String(primaryHost) + "' required>");
  webClient.println("</div>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>Secondary Server</label>");
  webClient.println("<input type='text' class='form-control' "
                    "name='host2' value='" +
                    String(secondaryHost) + "'>");
  webClient.println("</div>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>Client ID</label>");
  webClient.println("<input type='text' class='form-control' "
                    "name='id' value='" +
                    clientID + "' required>");
  webClient.println("</div>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>IP Mode</label><br>");
  webClient.println("<input type='radio' name='dhcp' value='1' " +
                    String(useDHCP ? "checked" : "") +
                    "> DHCP ");
  webClient.println("<input type='radio' name='dhcp' value='0' " +
                    String(!useDHCP ? "checked" : "") +
                    "> Static");
  webClient.println("</div>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>Static IP</label>");
  webClient.println("<input class='form-control' name='ip' value='" +
                    staticIP.toString() + "'>");
  webClient.println("</div>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>Gateway</label>");
  webClient.println("<input class='form-control' name='gw' value='" +
                    gateway.toString() + "'>");
  webClient.println("</div>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>Subnet</label>");
  webClient.println("<input class='form-control' name='sn' value='" +
                    subnet.toString() + "'>");
  webClient.println("</div>");

  webClient.println("<div class='form-group'>");
  webClient.println("<label>DNS</label>");
  webClient.println("<input class='form-control' name='dns' value='" +
                    dns.toString() + "'>");
  webClient.println("</div>");

  webClient.println("<button type='submit' class='btn btn-primary'>Save</button>");
  webClient.println(" <a class='btn btn-primary' href='http://" +
                    String(primaryHost) +
                    "'>Iceburg Server Login</a>");

  webClient.println("</form>");
  webClient.println("</div>");
  webClient.println("</body>");
  webClient.println("</html>");

  webClient.stop();
}
void fetchTallies()
{
  if (!connectToServer()) return;

  client.print("GET /tally/gettallystatus.php?id="+clientID+" HTTP/1.0\r\n\r\n");

  while (client.connected()) {
    String line = client.readStringUntil('\n');
    if (line == "\r") break;
  }

  StaticJsonDocument<2048> doc;

  if (deserializeJson(doc, client)) {
    client.stop();
    return;
  }

  JsonObject outputs = doc["outputs"];

  for(int i=1;i<=40;i++)
  {
    String key = String(i);

    // ONLY update if server actually sent this channel
    if(outputs.containsKey(key))
    {
      bool state = outputs[key];

      if(i <= 8)
      {
        setRelay(i-1, state);
      }
      else
      {
        tslTallies[i-9] = state;
      }
    }
  }

  client.stop();
}

/* ---------- SEND INPUTS ---------- */
void sendInputs() {

  bool anyChange = false;
  for (int i=0;i<8;i++)
    if (lastInputState[i]!=lastSentState[i]) anyChange=true;

  if (!anyChange) return;
  if (!connectToServer()) return;

  String url="/tally/settallystatus.php?id="+clientID+"&IP="+Ethernet.localIP().toString();
  Serial.println(url);
  for (int i=0;i<8;i++)
    if (lastInputState[i]!=lastSentState[i])
      url+="&ch"+String(i+1)+"="+String(lastInputState[i]);

  client.print("GET "+url+" HTTP/1.0\r\n\r\n");

  while (client.connected())
    while (client.available()) client.read();

  client.stop();

  for(int i=0;i<8;i++)
    lastSentState[i]=lastInputState[i];
}

/* ---------- POLL OUTPUTS ---------- */
void pollOutputs() {

  if (!connectToServer()) return;

  client.print("GET /tally/gettallystatus.php?id="+clientID+"&IP="+Ethernet.localIP().toString()+" HTTP/1.0\r\n\r\n");

  while (client.connected()) {
    String line = client.readStringUntil('\n');
    if (line == "\r") break;
  }

  StaticJsonDocument<512> doc;
  if (deserializeJson(doc, client)) {
    client.stop();
    return;
  }

  JsonObject outputs = doc["outputs"];

  for (int i=0;i<8;i++){
  setRelay(i, outputs[String(i+1)] | false);

}

   

  client.stop();
}

/* ---------- SETUP ---------- */
void setup() {

  Serial.begin(115200);
  pixels.begin();
  pixels.clear();
  pixels.show();

  loadConfig();

  for(int i=0;i<8;i++){
    pinMode(inputPins[i], INPUT_PULLUP);
    lastInputState[i]=digitalRead(inputPins[i]);
  }

  Wire.begin(SDA_PIN,SCL_PIN);
  writeTCA(REG_CONFIG,0x00);

  generateEthernetMAC(mac);

  SPI.begin(SPI_SCK,SPI_MISO,SPI_MOSI);
  Ethernet.init(W5500_CS);

  if (useDHCP)
    Ethernet.begin(mac);
  else
    Ethernet.begin(mac,staticIP,dns,gateway,subnet);

  webServer.begin();
  Serial.print("Web UI: http://");
   Serial.println(Ethernet.localIP());
  setPixelGreen();



RS485Serial.begin(38400, SERIAL_8E1, RS485_RX, RS485_TX);
for(int i=0;i<32;i++){
  strcpy(umdLabels[i],"No Connection");
  tslTallies[i] = false;
}
}

/* ---------- LOOP ---------- */
void loop() {

  handleWeb();

  bool changed=false;

  for(int i=0;i<8;i++){
    bool current=!digitalRead(inputPins[i]);
    if(current!=lastInputState[i]){
      lastInputState[i]=current;
      changed=true;
    }
  }

  if(changed) sendInputs();

  if(millis()-lastGetPoll>=getInterval){
    lastGetPoll=millis();
    pollOutputs();
  }
if (millis() - lastTallyPoll >= TALLY_INTERVAL)
  {
    lastTallyPoll = millis();
    fetchTallies();
    updateAllTSL();
  }

  // Poll UMD labels every second
  if (millis() - lastUMDPoll >= UMD_INTERVAL)
  {
    lastUMDPoll = millis();
    fetchUMD();
  }


if (millis() - lastrecovery >= recovery_INTERVAL)
  {
    lastrecovery = millis();
      checkPrimaryRecovery();
  }


  delay(5);
}
