'use strict';

const lsp = require('vscode-languageserver/node');
const { TextDocument } = require('vscode-languageserver-textdocument');
const html = require('vscode-html-languageservice');
const css = require('vscode-css-languageservice');

const language = process.argv.includes('--css') ? 'css' : 'html';
const service = language === 'css' ? css.getCSSLanguageService() : html.getLanguageService();
const connection = lsp.createConnection(lsp.ProposedFeatures.all);
const documents = new lsp.TextDocuments(TextDocument);
const document = params => documents.get(params.textDocument.uri);
const parsed = item => language === 'css' ? service.parseStylesheet(item) : service.parseHTMLDocument(item);

connection.onInitialize(() => ({ capabilities: {
  textDocumentSync: lsp.TextDocumentSyncKind.Incremental,
  completionProvider: { resolveProvider: false, triggerCharacters: language === 'css' ? ['/', '-', ':'] : ['.', ':', '<', '"', '=', '/'] },
  hoverProvider: true,
  documentFormattingProvider: true,
  documentRangeFormattingProvider: true,
  documentSymbolProvider: true,
  foldingRangeProvider: true,
  selectionRangeProvider: true
}}));
connection.onCompletion(params => {
  const item = document(params);
  return item ? service.doComplete(item, params.position, parsed(item)) : null;
});
connection.onHover(params => { const item = document(params); return item ? service.doHover(item, params.position, parsed(item)) : null; });
connection.onDocumentFormatting(params => { const item = document(params); return item ? service.format(item, undefined, params.options) : []; });
connection.onDocumentRangeFormatting(params => { const item = document(params); return item ? service.format(item, params.range, params.options) : []; });
connection.onDocumentSymbol(params => { const item = document(params); return item ? service.findDocumentSymbols(item, parsed(item)) : []; });
connection.onFoldingRanges(params => { const item = document(params); return item ? service.getFoldingRanges(item) : []; });
connection.onSelectionRanges(params => { const item = document(params); return item
  ? service.getSelectionRanges(item, params.positions, language === 'css' ? parsed(item) : undefined) : []; });
documents.onDidChangeContent(change => connection.sendDiagnostics({ uri: change.document.uri,
  diagnostics: language === 'css' ? service.doValidation(change.document, parsed(change.document)) : [] }));
documents.onDidClose(change => connection.sendDiagnostics({ uri: change.document.uri, diagnostics: [] }));
documents.listen(connection);
connection.listen();
