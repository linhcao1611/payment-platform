import { PaymentsList } from './components/PaymentsList'
import { PaymentDetail } from './components/PaymentDetail'
import { DemoTrafficToggle } from './components/DemoTrafficToggle'
import { matchPaymentDetail, useRoute } from './lib/useRoute'

export default function App() {
  const { path, navigate } = useRoute()
  const detailId = matchPaymentDetail(path)

  return (
    <main className="app">
      <DemoTrafficToggle />
      {detailId === null ? (
        <PaymentsList onOpen={(id) => navigate(`/payments/${id}`)} />
      ) : (
        <PaymentDetail id={detailId} onBack={() => navigate('/')} />
      )}
    </main>
  )
}
