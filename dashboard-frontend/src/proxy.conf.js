const PROXY_CONFIG = {
  "/api": {
    "target": "https://localhost:5111",
    "secure": false,
    "changeOrigin": true
  }
};
module.exports = PROXY_CONFIG;   