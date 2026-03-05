"""Simple HTTP server for the WASM AppBundle with correct MIME types."""
import http.server
import os

os.chdir(os.path.join(os.path.dirname(__file__), "bin/Debug/net10.0/browser-wasm/AppBundle"))
print(f"Serving from: {os.getcwd()}")
http.server.test(HandlerClass=http.server.SimpleHTTPRequestHandler, port=8080, bind="")
