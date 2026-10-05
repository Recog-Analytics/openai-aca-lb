import recogMark from "../assets/recog-mark.svg?url";

/** The brand's loading page, as in the ops portal: the Recog mark alone on the background until the first frame arrives. */
export function LoadingState() {
  return (
    <div role="status" className="loading" aria-label="Loading the load balancer dashboard">
      <img src={recogMark} alt="" />
    </div>
  );
}
