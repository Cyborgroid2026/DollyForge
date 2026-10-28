// vscode://dollyforge.dollyforge-browser/open?url=http://127.0.0.1:7780/ 를 받으면
// WebviewPanel 안의 iframe(allow="pointer-lock")으로 연다.
// Simple Browser 와 달리 Pointer Lock 이 작동하므로 3D 시점 조작이 된다.
const vscode = require('vscode');

let currentPanel = null;

exports.activate = (context) => {
  context.subscriptions.push(vscode.window.registerUriHandler({
    async handleUri(uri) {
      const url = new URLSearchParams(uri.query).get('url');
      if (!url || !/^http:\/\/(127\.0\.0\.1|localhost)(:\d+)?\//.test(url)) return;
      openGamePanel(url);
    },
  }));
};

function openGamePanel(url) {
  if (currentPanel) {
    currentPanel.reveal(vscode.ViewColumn.One);
    currentPanel.webview.html = getWebviewContent(url);
    return;
  }

  currentPanel = vscode.window.createWebviewPanel(
    'dollyforge-game',
    'Dolly Forge',
    vscode.ViewColumn.One,
    { enableScripts: true, retainContextWhenHidden: true }
  );

  currentPanel.webview.html = getWebviewContent(url);
  currentPanel.onDidDispose(() => { currentPanel = null; });
}

function getWebviewContent(url) {
  return `<!DOCTYPE html>
<html lang="ko">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy"
  content="default-src 'none'; frame-src http://127.0.0.1:* http://localhost:*; style-src 'unsafe-inline';">
<style>
  html, body { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #0b0c0e; }
  iframe { border: none; width: 100%; height: 100%; display: block; }
</style>
</head>
<body>
<iframe src="${url}" allow="pointer-lock; autoplay" allowfullscreen></iframe>
</body>
</html>`;
}
