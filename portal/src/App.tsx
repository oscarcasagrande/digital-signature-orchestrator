import { NavLink, Route, Routes, useLocation } from 'react-router-dom';
import { OperatorBar } from './components/OperatorBar';
import { useOperator } from './components/OperatorContext';
import { AuthCallback } from './pages/AuthCallback';
import { Login } from './pages/Login';
import { DeadLetters } from './pages/DeadLetters';
import { ProcessDetail } from './pages/ProcessDetail';
import { NewProcess } from './pages/NewProcess';
import { ProcessList } from './pages/ProcessList';

export function App() {
  const { loginRequired, session } = useOperator();
  const location = useLocation();
  return (
    <>
      <a href="#main" className="skip">Skip to content</a>
      <header className="topbar">
        <div className="brand">Signature Operations</div>
        <nav aria-label="Main">
          <NavLink to="/" end>Processes</NavLink>
          <NavLink to="/dead-letters">Dead letters</NavLink>
        </nav>
        <OperatorBar />
      </header>
      <main id="main">
        {loginRequired && !session && !location.pathname.startsWith('/auth/callback') ? <Login /> : (
        <Routes>
          <Route path="/" element={<ProcessList />} />
          <Route path="/processes/new" element={<NewProcess />} />
          <Route path="/processes/:id" element={<ProcessDetail />} />
          <Route path="/dead-letters" element={<DeadLetters />} />
          <Route path="/auth/callback" element={<AuthCallback />} />
          <Route path="*" element={<p className="empty">Page not found.</p>} />
        </Routes>
        )}
      </main>
    </>
  );
}
