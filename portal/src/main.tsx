import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter } from 'react-router-dom';
import { App } from './App';
import { OperatorProvider } from './components/OperatorContext';
import './styles.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <OperatorProvider>
        <App />
      </OperatorProvider>
    </BrowserRouter>
  </StrictMode>,
);
