// vscode://dollyforge.dollyforge-browser/open?url=http://127.0.0.1:7780/ 를 받으면
// stream.html 을 게임 서버에서 가져와 WebviewPanel 에 직접 표시한다.
// iframe 중첩 없이 최상위 문서로 로드하므로 setPointerCapture 가 패널 전체에서 동작한다.
const vscode = require('vscode');
const http = require('http');

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
  const parsed = new URL(url);

  http.get(url, (res) => {
    let body = '';
    res.on('data', chunk => body += chunk);
    res.on('end', () => {
      // location.host → 실제 호스트로 교체 (WebSocket 연결용)
      let html = body.replace('location.host', JSON.stringify(parsed.host));

      // Webview CSP: WebSocket·blob·인라인 스크립트 허용
      const csp = `<meta http-equiv="Content-Security-Policy" content="default-src 'none'; connect-src ws://${parsed.host} wss://${parsed.host}; img-src blob: data:; media-src blob: data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'; font-src https:;">`;
      html = html.replace('<head>', '<head>\n' + csp);

      if (currentPanel) {
        currentPanel.reveal(vscode.ViewColumn.One);
        currentPanel.webview.html = '';          // 강제 새로고침
        setTimeout(() => { currentPanel.webview.html = html; }, 50);
        return;
      }

      currentPanel = vscode.window.createWebviewPanel(
        'dollyforge-game',
        'Dolly Forge',
        vscode.ViewColumn.One,
        { enableScripts: true, retainContextWhenHidden: true }
      );

      currentPanel.webview.html = html;
      currentPanel.onDidDispose(() => { currentPanel = null; });
    });
  }).on('error', () => {
    // 게임 서버에 연결할 수 없으면 Simple Browser 로 폴백
    vscode.commands.executeCommand('simpleBrowser.show', url).catch(() => {});
  });
}
