'use strict';

const lsp = require('vscode-languageserver/node');
const { TextDocument } = require('vscode-languageserver-textdocument');
const html = require('vscode-html-languageservice');
const css = require('vscode-css-languageservice');

const language = process.argv.includes('--css') ? 'css' : 'html';
const service = language === 'css' ? css.getCSSLanguageService() : html.getLanguageService();
const connection = lsp.createConnection(lsp.ProposedFeatures.all);
const documents = new lsp.TextDocuments(TextDocument);
const getDocument = params => documents.get(params.textDocument.uri);
const parse = document => language === 'css' ? service.parseStylesheet(document) : service.parseHTMLDocument(document);

connection.onInitialize(() => ({
    capabilities: {
        textDocumentSync: lsp.TextDocumentSyncKind.Incremental,
        completionProvider: {
            resolveProvider: false,
            triggerCharacters: language === 'css' ? ['/', '-', ':'] : ['.', ':', '<', '"', '=', '/']
        },
        hoverProvider: true,
        documentFormattingProvider: true,
        documentRangeFormattingProvider: true,
        documentSymbolProvider: true,
        foldingRangeProvider: true,
        selectionRangeProvider: true
    }
}));

connection.onCompletion(params => {
    const document = getDocument(params);
    return document ? service.doComplete(document, params.position, parse(document)) : null;
});
connection.onHover(params => {
    const document = getDocument(params);
    return document ? service.doHover(document, params.position, parse(document)) : null;
});
connection.onDocumentFormatting(params => {
    const document = getDocument(params);
    return document ? service.format(document, undefined, params.options) : [];
});
connection.onDocumentRangeFormatting(params => {
    const document = getDocument(params);
    return document ? service.format(document, params.range, params.options) : [];
});
connection.onDocumentSymbol(params => {
    const document = getDocument(params);
    return document ? service.findDocumentSymbols(document, parse(document)) : [];
});
connection.onFoldingRanges(params => {
    const document = getDocument(params);
    return document ? service.getFoldingRanges(document, parse(document)) : [];
});
connection.onSelectionRanges(params => {
    const document = getDocument(params);
    return document
        ? service.getSelectionRanges(document, params.positions, language === 'css' ? parse(document) : undefined)
        : [];
});
documents.onDidChangeContent(change => connection.sendDiagnostics({
    uri: change.document.uri,
    diagnostics: language === 'css' ? service.doValidation(change.document, parse(change.document)) : []
}));
documents.onDidClose(change => connection.sendDiagnostics({ uri: change.document.uri, diagnostics: [] }));
documents.listen(connection);
connection.listen();
