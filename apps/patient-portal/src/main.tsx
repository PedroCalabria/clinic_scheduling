import { createI18n } from '@clinic/shared';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { I18nextProvider } from 'react-i18next';
import { BrowserRouter } from 'react-router';
import { App } from './App';
import { ROUTER_BASENAME } from './config/basePath';
import './index.css';

const queryClient = new QueryClient();
const i18n = createI18n();

const container = document.getElementById('root');

if (!container) {
  throw new Error('Root container #root not found in index.html.');
}

createRoot(container).render(
  <StrictMode>
    <I18nextProvider i18n={i18n}>
      <QueryClientProvider client={queryClient}>
        {/* basename derives from the same constant as Vite's base (design D1). */}
        <BrowserRouter basename={ROUTER_BASENAME}>
          <App />
        </BrowserRouter>
      </QueryClientProvider>
    </I18nextProvider>
  </StrictMode>,
);
